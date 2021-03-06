﻿// Copyright 2018 Fabulous contributors. See LICENSE.md for license.

// F# PortaCode command processing (e.g. used by Fabulous.Cli)

module FSharp.Compiler.PortaCode.ProcessCommandLine

open FSharp.Compiler.PortaCode.CodeModel
open FSharp.Compiler.PortaCode.Interpreter
open FSharp.Compiler.PortaCode.FromCompilerService
open System
open System.IO
open Microsoft.FSharp.Compiler.SourceCodeServices
open System.Net
open System.Text


let checker = FSharpChecker.Create(keepAssemblyContents = true)

let ProcessCommandLine (argv: string[]) =
    let mutable fsproj = None
    let mutable eval = false
    let mutable watch = false
    let mutable writeinfo = false
    let mutable webhook = None
    let mutable otherFlags = []
    let args = 
        let mutable haveDashes = false

        [| for arg in argv do 
                let arg = arg.Trim()
                if arg.StartsWith("@") then 
                    for line in File.ReadAllLines(arg.[1..]) do 
                        let line = line.Trim()
                        if not (String.IsNullOrWhiteSpace(line)) then
                            yield line
                elif arg.EndsWith(".fsproj") then 
                    fsproj <- Some arg
                elif arg = "--" then haveDashes <- true
                elif arg.StartsWith "--define:" then otherFlags <- otherFlags @ [ arg ]
                elif arg = "--watch" then watch <- true
                elif arg = "--eval" then eval <- true
                elif arg = "--writeinfo" then writeinfo <- true
                elif arg.StartsWith "--webhook:" then webhook  <- Some arg.["--webhook:".Length ..]
                else yield arg  |]

    if args.Length = 0 && fsproj.IsNone then 
        match Seq.toList (Directory.EnumerateFiles(Environment.CurrentDirectory, "*.fsproj")) with 
        | [ ] -> 
            failwith "no project file found, no compilation arguments given" 
        | [ file ] -> 
            printfn "fscd: using implicit project file '%s'" file
            fsproj <- Some file
        | _ -> 
            failwith "multiple project files found" 

    let options = 
        match fsproj with 
        | Some fsprojFile -> 
            if args.Length > 1 then failwith "can't give both project file and compilation arguments"
            match FSharpDaemon.ProjectCracker.load (new System.Collections.Concurrent.ConcurrentDictionary<_,_>()) fsprojFile with 
            | Ok (options, sourceFiles, _log) -> 
                let options = { options with SourceFiles = Array.ofList sourceFiles }
                let sourceFilesSet = Set.ofList sourceFiles
                let options = { options with OtherOptions = options.OtherOptions |> Array.filter (fun s -> not (sourceFilesSet.Contains(s))) }
                Result.Ok options
            | Error err -> 
                failwithf "Couldn't parse project file: %A" err
            
        | None -> 
            let sourceFiles, otherFlags2 = args |> Array.partition (fun arg -> arg.EndsWith(".fs") || arg.EndsWith(".fsi") || arg.EndsWith(".fsx"))
            let otherFlags = [| yield! otherFlags; yield! otherFlags2 |]
            let sourceFiles = sourceFiles |> Array.map Path.GetFullPath 
            printfn "CurrentDirectory = %s" Environment.CurrentDirectory
        
            match sourceFiles with 
            | [| script |] when script.EndsWith(".fsx") ->
                let text = File.ReadAllText script
                let options, errors = checker.GetProjectOptionsFromScript(script, text, otherFlags=otherFlags) |> Async.RunSynchronously
                if errors.Length > 0 then 
                    for error in errors do 
                        printfn "%s" (error.ToString())
                    Result.Error ()
                else                                
                    let options = { options with SourceFiles = sourceFiles }
                    Result.Ok options
            | _ -> 
                let options = checker.GetProjectOptionsFromCommandLineArgs("tmp.fsproj", otherFlags)
                let options = { options with SourceFiles = sourceFiles }
                Result.Ok options

    match options with 
    | Result.Error () -> 
        printfn "fscd: error processing project options or script" 
        -1
    | Result.Ok options ->
    let options = { options with OtherOptions = Array.append options.OtherOptions (Array.ofList otherFlags) }
    //printfn "options = %A" options

    let rec checkFile count sourceFile =         
        try 
            let _, checkResults = checker.ParseAndCheckFileInProject(sourceFile, 0, File.ReadAllText(sourceFile), options) |> Async.RunSynchronously  
            match checkResults with 
            | FSharpCheckFileAnswer.Aborted -> 
                printfn "aborted"
                Result.Error ()
            | FSharpCheckFileAnswer.Succeeded res -> 
                let mutable hasErrors = false
                for error in res.Errors do 
                    printfn "%s" (error.ToString())
                    if error.Severity = FSharpErrorSeverity.Error then 
                        hasErrors <- true
                if hasErrors then 
                    Result.Error ()
                else
                    Result.Ok res.ImplementationFile 
        with 
        | :? System.IO.IOException when count = 0 -> System.Threading.Thread.Sleep 500; checkFile 1 sourceFile
        | exn -> 
            printfn "%s" (exn.ToString())
            Result.Error ()

    let convFile (i: FSharpImplementationFileContents) =         
        //(i.QualifiedName, i.FileName
        i.FileName, { Code = convDecls i.Declarations }

    let checkFiles files =             
        let rec loop rest acc = 
            match rest with 
            | file :: rest -> 
                match checkFile 0 (Path.GetFullPath(file)) with 
                | Result.Error () -> 
                    printfn "fscd: ERRORS for %s" file
                    Result.Error ()
                | Result.Ok iopt -> 
                    printfn "fscd: COMPILED %s" file
                    match iopt with 
                    | None -> Result.Error ()
                    | Some i -> 
                        printfn "fscd: GOT PortaCode for %s" file
                        loop rest (i :: acc)
            | [] -> Result.Ok (List.rev acc)
        loop (List.ofArray files) []

    let jsonFiles (impls: FSharpImplementationFileContents[]) =         
        let data = Array.map convFile impls
        let json = Newtonsoft.Json.JsonConvert.SerializeObject(data)
        json

    let sendToWebHook (hook: string) fileContents = 
        try 
            let json = jsonFiles (Array.ofList fileContents)
            printfn "fscd: GOT JSON, length = %d" json.Length
            use webClient = new WebClient(Encoding = Encoding.UTF8)
            printfn "fscd: SENDING TO WEBHOOK... " // : <<<%s>>>... --> %s" json.[0 .. min (json.Length - 1) 100] hook
            let resp = webClient.UploadString (hook,"Put",json)
            printfn "fscd: RESP FROM WEBHOOK: %s" resp
        with err -> 
            printfn "fscd: ERROR SENDING TO WEBHOOK: %A" (err.ToString())

    /// Write an info file containing extra information to make available to F# tooling
    let writeInfoFile tooltips sourceFile errors = 
        let infoDir = Path.Combine(Path.GetDirectoryName(sourceFile), ".fsharp")
        let infoFile = Path.Combine(infoDir, Path.GetFileName(sourceFile) + ".info")
        let lockFile = Path.Combine(infoDir, Path.GetFileName(sourceFile) + ".info.lock")
        printfn "writing info file %s..." infoFile 
        try 
            let lines = 
                [| for (x: DLocalDef, value: obj) in tooltips do
                        let range = x.Range
                        // TODO: this is a hack for TensorFlow.FSharp, consider how to generalize it
                        match value with 
                        | null -> ()
                        | value -> 
                            printfn "value %s " (value.GetType().Name) 
                            if value.GetType().Name = "DT`1" then 
                                let msg = sprintf "%A" (value.ToString()) 
                                let line = sprintf "ToolTip\t%d\t%d\t%d\t%d\t%s" range.StartLine range.StartColumn range.EndLine range.EndColumn msg
                                yield line
                   for (exn:exn, rangeStack) in errors do 
                        if List.length rangeStack > 0 then 
                            let range = List.last rangeStack 
                            let message = "Live check failed: " + exn.Message.Replace("\t"," ").Replace("\r","   ").Replace("\n","   ") 
                            let msg = sprintf "Error\t%d\t%d\t%d\t%d\terror\t%s\t304" range.StartLine range.StartColumn range.EndLine range.EndColumn message
                            yield msg |]

            if not (Directory.Exists infoDir) then 
                Directory.CreateDirectory(infoDir) |> ignore

            File.WriteAllLines(lockFile, [ sprintf "locked at %A by %s" System.DateTime.Now __SOURCE_FILE__ ])
            File.WriteAllLines(infoFile, lines)
        finally
            try if Directory.Exists infoDir && File.Exists lockFile then File.Delete lockFile with _ -> ()

    let evaluateDecls fileContents = 
        let assemblyTable = 
            dict [| for r in options.OtherOptions do 
                        if r.StartsWith("-r:") && not (r.Contains(".NETFramework")) then 
                            let assemName = r.[3..]
                            printfn "Script: pre-loading referenced assembly %s " assemName
                            match System.Reflection.Assembly.LoadFrom(assemName) with 
                            | null -> 
                                printfn "Script: failed to pre-load referenced assembly %s " assemName
                            | asm -> 
                                let name = asm.GetName()
                                yield (name.Name, asm) |]

        let assemblyResolver (nm: Reflection.AssemblyName) =  
            match assemblyTable.TryGetValue(nm.Name) with
            | true, res -> res
            | _ -> Reflection.Assembly.Load(nm)
                                        
        let tooltips = ResizeArray()
        let sink =
            if writeinfo then 
                { new Sink with 
                     member __.BindLocal(x: DLocalDef, value: obj) = tooltips.Add ((x, value)) }
                |> Some
            else  
                None

        let ctxt = EvalContext(assemblyResolver, ?sink=sink)
        let fileConvContents = [| for i in fileContents -> convFile i |]

        for (_, contents) in fileConvContents do 
            ctxt.AddDecls(contents.Code)

        for (sourceFile, ds) in fileConvContents do 
            printfn "evaluating decls.... " 
            let errors = ctxt.TryEvalDecls (envEmpty, ds.Code)

            if writeinfo then 
                writeInfoFile (tooltips.ToArray()) sourceFile errors

            printfn "...evaluated decls" 

    let changed why _ =
        try 
            printfn "fscd: CHANGE DETECTED (%s), COMPILING...." why

            match checkFiles options.SourceFiles with 
            | Result.Error () -> ()
            | Result.Ok allFileContents -> 

            match webhook with 
            | Some hook -> sendToWebHook hook allFileContents
            | None -> 

            if eval then 
                printfn "fscd: CHANGE DETECTED, RE-EVALUATING ALL INPUTS...." 
                evaluateDecls allFileContents 

            // The default is to dump
            if not eval && webhook.IsNone then 
                let fileConvContents = jsonFiles (Array.ofList allFileContents)

                printfn "%A" fileConvContents
        with err -> 
            printfn "fscd: exception: %A" (err.ToString())

    for o in options.OtherOptions do 
        printfn "compiling, option %s" o

    if watch then 
        // Send an immediate changed() event
        if webhook.IsNone then 
            printfn "Sending initial changes... " 
            for sourceFile in options.SourceFiles do
                changed "initial" ()

        let watchers = 
            [ for sourceFile in options.SourceFiles do
                let path = Path.GetDirectoryName(sourceFile)
                let fileName = Path.GetFileName(sourceFile)
                printfn "fscd: WATCHING %s in %s" fileName path 
                let watcher = new FileSystemWatcher(path, fileName)
                watcher.NotifyFilter <- NotifyFilters.Attributes ||| NotifyFilters.CreationTime ||| NotifyFilters.FileName ||| NotifyFilters.LastAccess ||| NotifyFilters.LastWrite ||| NotifyFilters.Size ||| NotifyFilters.Security;
                watcher.Changed.Add (changed "Changed")
                watcher.Created.Add (changed "Created")
                watcher.Deleted.Add (changed "Deleted")
                watcher.Renamed.Add (changed "Renamed")
                yield watcher ]

        for watcher in watchers do
            watcher.EnableRaisingEvents <- true

        printfn "Waiting for changes... press any key to exit" 
        System.Console.ReadLine() |> ignore
        for watcher in watchers do
            watcher.EnableRaisingEvents <- false

    else
        changed "once" ()
    0

