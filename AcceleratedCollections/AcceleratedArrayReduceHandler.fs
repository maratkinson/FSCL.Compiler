﻿namespace FSCL.Compiler.Plugins.AcceleratedCollections

open FSCL.Compiler
open FSCL.Compiler.Language
open System.Reflection
open System.Reflection.Emit
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Core.LanguagePrimitives
open System.Collections.Generic
open System
open FSCL.Compiler.Util
open Microsoft.FSharp.Reflection
open AcceleratedCollectionUtil
open System.Runtime.InteropServices
open Microsoft.FSharp.Linq.RuntimeHelpers

type AcceleratedArrayReduceHandler() =
    let placeholderComp (a:int) (b:int) =
        a + b
        (*__kernel
void reduce(__global float* buffer,
            __const int block,
            __const int length,
            __global float* result) {

  int global_index = get_global_id(0) * block;
  float accumulator = INFINITY;
  int upper_bound = (get_global_id(0) + 1) * block;
  if (upper_bound > length) upper_bound = length;
  while (global_index < upper_bound) {
    float element = buffer[global_index];
    accumulator = (accumulator < element) ? accumulator : element;
    global_index++;
  }
  result[get_group_id(0)] = accumulator;
}*)
    let cpu_template = 
        <@
            fun(g_idata:int[], g_odata:int[], block: int) ->
                let mutable global_index = get_global_id(0) * block
                let mutable upper_bound = (get_global_id(0) + 1) * block
                if upper_bound > g_idata.Length then
                    upper_bound <- g_idata.Length

                // We don't know which is the neutral value for placeholderComp so we need to
                // initialize it with an element of the input array
                let mutable accumulator = 0
                if global_index < upper_bound then
                    accumulator <- g_idata.[global_index]
                    global_index <- global_index + 1

                while global_index < upper_bound do
                    accumulator <- placeholderComp accumulator g_idata.[global_index]
                    global_index <- global_index + 1

                g_odata.[get_group_id(0)] <- accumulator
        @>

    // NEW: Two-Stage reduction instead of multi-stage
    let gpu_template = 
        <@
            fun(g_idata:int[], [<Local>]sdata:int[], g_odata:int[]) ->
                let mutable global_index = get_global_id(0)

                let mutable accumulator = 0
                while (global_index < g_idata.Length) do
                    accumulator <- placeholderComp accumulator g_idata.[global_index]
                    global_index <- global_index + get_global_size(0)

                let local_index = get_local_id(0)
                sdata.[local_index] <- accumulator
                barrier(CLK_LOCAL_MEM_FENCE)

                let mutable offset = get_local_size(0) / 2
                while(offset > 0) do
                    if(local_index < offset) then
                        sdata.[local_index] <- placeholderComp (sdata.[local_index]) (sdata.[local_index + offset])
                    offset <- offset / 2
                    barrier(CLK_LOCAL_MEM_FENCE)
                
                if local_index = 0 then
                    g_odata.[get_group_id(0)] <- sdata.[0]
                (*
                let tid = get_local_id(0)
                let i = get_group_id(0) * (get_local_size(0) * 2) + get_local_id(0)

                if(i < g_idata.Length) then 
                    sdata.[tid] <- g_idata.[i] 
                else 
                    sdata.[tid] <- 0
                if (i + get_local_size(0) < g_idata.Length) then 
                    sdata.[tid] <- placeholderComp (sdata.[tid]) (g_idata.[i + get_local_size(0)])

                barrier(CLK_LOCAL_MEM_FENCE)
                // do reduction in shared mem
                let mutable s = get_local_size(0) >>> 1
                while (s > 0) do 
                    if (tid < s) then
                        sdata.[tid] <- placeholderComp (sdata.[tid]) (sdata.[tid + s])
                    barrier(CLK_LOCAL_MEM_FENCE)
                    s <- s >>> 1

                if (tid = 0) then 
                    g_odata.[get_group_id(0)] <- sdata.[0]
                *)
        @>
             
    let rec SubstitutePlaceholders(e:Expr, parameters:Dictionary<Var, Var>, accumulatorPlaceholder:Var, actualFunction: MethodInfo) =  
        // Build a call expr
        let RebuildCall(o:Expr option, m: MethodInfo, args:Expr list) =
            if o.IsSome && (not m.IsStatic) then
                Expr.Call(o.Value, m, List.map(fun (e:Expr) -> SubstitutePlaceholders(e, parameters, accumulatorPlaceholder, actualFunction)) args)
            else
                Expr.Call(m, List.map(fun (e:Expr) -> SubstitutePlaceholders(e, parameters, accumulatorPlaceholder, actualFunction)) args)  
            
        match e with
        | Patterns.Var(v) ->       
            // Substitute parameter with the new one (of the correct type)
            if v.Name = "accumulator" then
                Expr.Var(accumulatorPlaceholder)
            else if parameters.ContainsKey(v) then
                Expr.Var(parameters.[v])
            else
                e
        | Patterns.Call(o, m, args) ->   
            // If this is the placeholder for the utility function (to be applied to each pari of elements)         
            if m.Name = "placeholderComp" then
                RebuildCall(o, actualFunction, List.map(fun (e:Expr) -> SubstitutePlaceholders(e, parameters, accumulatorPlaceholder, actualFunction)) args)
            // If this is an access to array (a parameter)
            else if m.DeclaringType.Name = "IntrinsicFunctions" then
                match args.[0] with
                | Patterns.Var(v) ->
                    if m.Name = "GetArray" then
                        // Find the placeholder holding the variable
                        if (parameters.ContainsKey(v)) then
                            // Recursively process the arguments, except the array reference
                            let arrayGet, _ = AcceleratedCollectionUtil.GetArrayAccessMethodInfo(parameters.[v].Type.GetElementType())
                            Expr.Call(arrayGet, [ Expr.Var(parameters.[v]); SubstitutePlaceholders(args.[1], parameters, accumulatorPlaceholder, actualFunction) ])
                        else
                            RebuildCall(o, m, args)
                    else if m.Name = "SetArray" then
                        // Find the placeholder holding the variable
                        if (parameters.ContainsKey(v)) then
                            // Recursively process the arguments, except the array reference)
                            let _, arraySet = AcceleratedCollectionUtil.GetArrayAccessMethodInfo(parameters.[v].Type.GetElementType())
                            // If the value is const (e.g. 0) then it must be converted to the new array element type
                            let newValue = match args.[2] with
                                            | Patterns.Value(o, t) ->
                                                let outputParameterType = actualFunction.GetParameters().[1].ParameterType
                                                // Conversion method (ToDouble, ToSingle, ToInt, ...)
                                                Expr.Value(Activator.CreateInstance(outputParameterType), outputParameterType)
                                            | _ ->
                                                SubstitutePlaceholders(args.[2], parameters, accumulatorPlaceholder, actualFunction)
                            Expr.Call(arraySet, [ Expr.Var(parameters.[v]); SubstitutePlaceholders(args.[1], parameters, accumulatorPlaceholder, actualFunction); newValue ])
                                                           
                        else
                            RebuildCall(o, m, args)
                    else
                         RebuildCall(o, m,args)
                | _ ->
                    RebuildCall(o, m, List.map(fun (e:Expr) -> SubstitutePlaceholders(e, parameters, accumulatorPlaceholder, actualFunction)) args)                  
            // Otherwise process children and return the same call
            else
                RebuildCall(o, m, List.map(fun (e:Expr) -> SubstitutePlaceholders(e, parameters, accumulatorPlaceholder, actualFunction)) args)
        | Patterns.Let(v, value, body) ->
            if v.Name = "accumulator" then
                Expr.Let(accumulatorPlaceholder, Expr.Coerce(value, accumulatorPlaceholder.Type), SubstitutePlaceholders(body, parameters, accumulatorPlaceholder, actualFunction))
            // a and b are "special" vars that hold the params of the reduce function
            else if v.Name = "a" then
                let a = Quotations.Var("a", actualFunction.GetParameters().[0].ParameterType, false)
                parameters.Add(v, a)
                Expr.Let(a, SubstitutePlaceholders(value, parameters, accumulatorPlaceholder, actualFunction), 
                            SubstitutePlaceholders(body, parameters, accumulatorPlaceholder, actualFunction))            
            else if v.Name = "b" then
                let b = Quotations.Var("b", actualFunction.GetParameters().[1].ParameterType, false)
                // Remember for successive references to a and b
                parameters.Add(v, b)
                Expr.Let(b, SubstitutePlaceholders(value, parameters, accumulatorPlaceholder, actualFunction), SubstitutePlaceholders(body, parameters, accumulatorPlaceholder, actualFunction))
            else
                Expr.Let(v, SubstitutePlaceholders(value, parameters, accumulatorPlaceholder, actualFunction), SubstitutePlaceholders(body, parameters, accumulatorPlaceholder, actualFunction))
        | ExprShape.ShapeLambda(v, b) ->
            Expr.Lambda(v, SubstitutePlaceholders(b, parameters, accumulatorPlaceholder, actualFunction))                    
        | ExprShape.ShapeCombination(o, l) ->
            match e with
            | Patterns.IfThenElse(cond, ifb, elseb) ->
                let nl = new List<Expr>();
                for e in l do 
                    let ne = SubstitutePlaceholders(e, parameters, accumulatorPlaceholder, actualFunction) 
                    // Trick to adapt "0" in (sdata.[tid] <- if(i < n) then g_idata.[i] else 0) in case of other type of values (double: 0.0)
                    nl.Add(ne)
                ExprShape.RebuildShapeCombination(o, List.ofSeq(nl))
            | _ ->
                let nl = new List<Expr>();
                for e in l do 
                    let ne = SubstitutePlaceholders(e, parameters, accumulatorPlaceholder, actualFunction) 
                    nl.Add(ne)
                ExprShape.RebuildShapeCombination(o, List.ofSeq(nl))
        | _ ->
            e

    let kernelName (prefix: string, parameterTypes: Type list, utilityFunction: string) =
        String.concat "_" ([prefix] @ (List.map (fun (t:Type) -> t.Name.Replace(".", "")) parameterTypes) @ [utilityFunction])

    member this.EvaluateAndApply(e:Expr) (a:obj) (b:obj) =
        let f = LeafExpressionConverter.EvaluateQuotation(e)
        let fm = f.GetType().GetMethod("Invoke")
        let r1 = fm.Invoke(f, [| a |])
        let r2m = r1.GetType().GetMethod("Invoke")
        let r2 = r2m.Invoke(r1, [| b |])
        r2

    interface IAcceleratedCollectionHandler with
        member this.Process(methodInfo, cleanArgs, root, meta, step) =       
            (*
                Array map looks like: Array.map fun collection
                At first we check if fun is a lambda (first argument)
                and in this case we transform it into a method
                Secondly, we iterate parsing on the second argument (collection)
                since it might be a subkernel
            *)
            let lambda, computationFunction =                
                AcceleratedCollectionUtil.ExtractComputationFunction(cleanArgs, root)
                                
            // Extract the reduce function 
            match computationFunction with
            | Some(functionInfo, body) ->
                // Create on-the-fly module to host the kernel                
                // The dynamic module that hosts the generated kernels
                let assemblyName = IDGenerator.GenerateUniqueID("FSCL.Compiler.Plugins.AcceleratedCollections.AcceleratedArray");
                let assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
                let moduleBuilder = assemblyBuilder.DefineDynamicModule("AcceleratedArrayModule");

                // Now create the kernel
                // We need to get the type of a array whose elements type is the same of the functionInfo parameter
                let inputArrayType = Array.CreateInstance(functionInfo.GetParameters().[0].ParameterType, 0).GetType()
                let outputArrayType = Array.CreateInstance(functionInfo.ReturnType, 0).GetType()
                // Now that we have the types of the input and output arrays, create placeholders (var) for the kernel input and output       
                
                // Check device target
                let targetType = meta.KernelMeta.Get<DeviceTypeAttribute>()
            
                let kModule = 
                    // GPU CODE
                    match targetType.Type with
                    | DeviceType.Gpu ->                    
                        // Now we can create the signature and define parameter name in the dynamic module
                        // DynamicMethod would be simpler and would not require a dynamic module but unfortunately it doesn't support
                        // Custom attributes for ites parameters. We instead have to mark the second parameter of the kernel with [Local]
                        let methodBuilder = moduleBuilder.DefineGlobalMethod(
                                                kernelName("ArrayReduce", 
                                                            [functionInfo.GetParameters().[0].ParameterType; 
                                                            functionInfo.GetParameters().[1].ParameterType],
                                                            functionInfo.Name), 
                                                MethodAttributes.Public ||| MethodAttributes.Static, typeof<unit>, 
                                                [| inputArrayType; outputArrayType; outputArrayType |])
                        let paramBuilder = methodBuilder.DefineParameter(1, ParameterAttributes.In, "input_array")
                        let paramBuilder = methodBuilder.DefineParameter(2, ParameterAttributes.In, "local_array")
                        let paramBuilder = methodBuilder.DefineParameter(3, ParameterAttributes.In, "output_array")
                        // Body (simple return) of the method must be set to build the module and get the MethodInfo that we need as signature
                        methodBuilder.GetILGenerator().Emit(OpCodes.Ret)
                        moduleBuilder.CreateGlobalFunctions()
                        let signature = moduleBuilder.GetMethod(methodBuilder.Name) 
                        
                        // Create parameters placeholders
                        let inputHolder = Quotations.Var("input_array", inputArrayType)
                        let localHolder = Quotations.Var("local_array", outputArrayType)
                        let outputHolder = Quotations.Var("output_array", outputArrayType)
                        let accumulatorPlaceholder = Quotations.Var("accumulator", outputArrayType.GetElementType())

                        // Finally, create the body of the kernel
                        let templateBody, templateParameters = AcceleratedCollectionUtil.GetKernelFromLambda(gpu_template)   
                        let parameterMatching = new Dictionary<Var, Var>()
                        parameterMatching.Add(templateParameters.[0], inputHolder)
                        parameterMatching.Add(templateParameters.[1], localHolder)
                        parameterMatching.Add(templateParameters.[2], outputHolder)

                        // Replace functions and references to parameters
                        let functionMatching = new Dictionary<string, MethodInfo>()
                        let newBody = SubstitutePlaceholders(templateBody, parameterMatching, accumulatorPlaceholder, functionInfo)  
                    
                        let kInfo = new AcceleratedKernelInfo(signature, 
                                                              newBody, 
                                                              meta, 
                                                              "Array.reduce", body)
                        let kernelModule = new KernelModule(kInfo, cleanArgs)
                        
                        // Store placeholders
                        (kernelModule.Kernel.OriginalParameters.[0] :?> FunctionParameter).Placeholder <- inputHolder
                        (kernelModule.Kernel.OriginalParameters.[1] :?> FunctionParameter).Placeholder <- localHolder
                        (kernelModule.Kernel.OriginalParameters.[2] :?> FunctionParameter).Placeholder <- outputHolder

                        kernelModule                
                    |_ ->
                        // CPU CODE                    
                        // Now we can create the signature and define parameter name in the dynamic module
                        // DynamicMethod would be simpler and would not require a dynamic module but unfortunately it doesn't support
                        // Custom attributes for ites parameters. We instead have to mark the second parameter of the kernel with [Local]
                        let methodBuilder = moduleBuilder.DefineGlobalMethod(
                                                kernelName("ArrayReduce", 
                                                            [functionInfo.GetParameters().[0].ParameterType; 
                                                            functionInfo.GetParameters().[1].ParameterType],
                                                            functionInfo.Name), 
                                                MethodAttributes.Public ||| MethodAttributes.Static, typeof<unit>, 
                                                [| inputArrayType; typeof<int>; outputArrayType |])
                        let paramBuilder = methodBuilder.DefineParameter(1, ParameterAttributes.In, "input_array")
                        let paramBuilder = methodBuilder.DefineParameter(2, ParameterAttributes.In, "output_array")
                        let paramBuilder = methodBuilder.DefineParameter(3, ParameterAttributes.In, "block")
                        // Body (simple return) of the method must be set to build the module and get the MethodInfo that we need as signature
                        methodBuilder.GetILGenerator().Emit(OpCodes.Ret)
                        moduleBuilder.CreateGlobalFunctions()
                        let signature = moduleBuilder.GetMethod(methodBuilder.Name) 
                    
                        // Create parameters placeholders
                        let inputHolder = Quotations.Var("input_array", inputArrayType)
                        let blockHolder = Quotations.Var("block", typeof<int>)
                        let outputHolder = Quotations.Var("output_array", outputArrayType)
                        let accumulatorPlaceholder = Quotations.Var("accumulator", outputArrayType.GetElementType())

                        // Finally, create the body of the kernel
                        let templateBody, templateParameters = AcceleratedCollectionUtil.GetKernelFromLambda(cpu_template)   
                        let parameterMatching = new Dictionary<Var, Var>()
                        parameterMatching.Add(templateParameters.[0], inputHolder)
                        parameterMatching.Add(templateParameters.[1], outputHolder)
                        parameterMatching.Add(templateParameters.[2], blockHolder)

                        // Replace functions and references to parameters
                        let functionMatching = new Dictionary<string, MethodInfo>()
                        let newBody = SubstitutePlaceholders(templateBody, parameterMatching, accumulatorPlaceholder, functionInfo)  
                    
                        // Setup kernel module and return
                        let kInfo = new AcceleratedKernelInfo(signature, 
                                                              newBody, 
                                                              meta, 
                                                              "Array.reduce", body)
                        let kernelModule = new KernelModule(kInfo, cleanArgs)
                        
                        // Store placeholders
                        (kernelModule.Kernel.OriginalParameters.[0] :?> FunctionParameter).Placeholder <- inputHolder
                        (kernelModule.Kernel.OriginalParameters.[1] :?> FunctionParameter).Placeholder <- outputHolder
                        (kernelModule.Kernel.OriginalParameters.[2] :?> FunctionParameter).Placeholder <- blockHolder

                        kernelModule 

                // Add applied function                                 
                let reduceFunctionInfo = new FunctionInfo(functionInfo, body, lambda.IsSome)
                
                // Store the called function (runtime execution will use it to perform latest iterations of reduction)
                if lambda.IsSome then
                    kModule.Kernel.CustomInfo.Add("ReduceFunction", lambda.Value)
                else
                    kModule.Kernel.CustomInfo.Add("ReduceFunction", fst computationFunction.Value)
                                    
                // Store the called function (runtime execution will use it to perform latest iterations of reduction)
                kModule.Functions.Add(reduceFunctionInfo.ID, reduceFunctionInfo)
                // Return module                             
                Some(kModule)

            | _ ->
                None