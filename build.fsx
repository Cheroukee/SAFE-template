#r "paket: groupref build //"
#load "./.fake/build.fsx/intellisense.fsx"

#if !FAKE
#r "netstandard"
#r "Facades/netstandard" // https://github.com/ionide/ionide-vscode-fsharp/issues/839#issuecomment-396296095
#endif

open System

open Fake.Core
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Tools

let templatePath = "./Content/.template.config/template.json"
let templateProj = "SAFE.Template.proj"
let templateName = "SAFE-Stack Web App"
let nupkgDir = Path.getFullName "./nupkg"

let release = ReleaseNotes.load "RELEASE_NOTES.md"

let formattedRN =
    release.Notes
    |> List.map (sprintf "* %s")
    |> String.concat "\n"

Target.create "Clean" (fun _ ->
    Shell.cleanDirs [ nupkgDir ]
    Git.CommandHelper.directRunGitCommandAndFail "./Content" "clean -fxd"
)

Target.create "Pack" (fun _ ->
    Shell.regexReplaceInFileWithEncoding
        "  \"name\": .+,"
       ("  \"name\": \"" + templateName + " v" + release.NugetVersion + "\",")
        System.Text.Encoding.UTF8
        templatePath
    DotNet.pack
        (fun args ->
            { args with
                    OutputPath = Some nupkgDir
                    Common =
                        { args.Common with
                            CustomParams =
                                Some (sprintf "/p:PackageVersion=%s /p:PackageReleaseNotes=\"%s\""
                                        release.NugetVersion
                                        formattedRN) }
            })
        templateProj
)

Target.create "Install" (fun _ ->
    let args = sprintf "-i %s/SAFE.Template.%s.nupkg" nupkgDir release.NugetVersion
    let result = DotNet.exec (fun x -> { x with DotNetCliPath = "dotnet" }) "new" args
    if not result.OK then failwithf "`dotnet %s` failed" args
)

let psi exe arg dir (x: ProcStartInfo) : ProcStartInfo =
    { x with
        FileName = exe
        Arguments = arg
        WorkingDirectory = dir }

let run exe arg dir =
    let result = Process.execWithResult (psi exe arg dir) TimeSpan.MaxValue
    if not result.OK then (failwithf "`%s %s` failed: %A" exe arg result.Errors)

type BuildPaketDependencies =
    { Azure : bool }

    with override x.ToString () =
            if x.Azure then "azure" else "noazure"

type ClientPaketDependencies =
    { Remoting : bool
      Fulma : bool }

    with override x.ToString () =
            let remoting = if x.Remoting then "remoting" else "noremoting"
            let fulma = if x.Fulma then "fulma" else "nofulma"
            sprintf "%s-%s" remoting fulma

type ServerPaketDependency = Saturn | Giraffe | Suave

    with override x.ToString () =
            match x with
            | Saturn -> "saturn"
            | Giraffe -> "giraffe"
            | Suave -> "suave"

type ServerPaketDependencies =
    { Server : ServerPaketDependency
      Remoting : bool
      Azure : bool }

    with override x.ToString () =
            let server = string x.Server
            let remoting = if x.Remoting then "remoting" else "noremoting"
            let azure = if x.Azure then "azure" else "noazure"
            sprintf "%s-%s-%s" server remoting azure

type CombinedPaketDependencies =
    { Azure : bool
      Remoting : bool
      Fulma : bool
      Server : ServerPaketDependency }

    member x.ToBuild : BuildPaketDependencies =
            { Azure = x.Azure }

    member x.ToClient : ClientPaketDependencies =
        { Remoting = x.Remoting
          Fulma = x.Fulma }

    member x.ToServer : ServerPaketDependencies =
        { Server = x.Server
          Remoting = x.Remoting
          Azure = x.Azure }

    override x.ToString () =
        let remoting = if x.Remoting then Some "--remoting" else None
        let azure = if x.Azure then Some "--deploy azure" else None
        let fulma = if not x.Fulma then Some "--layout none" else None
        let server = if x.Server <> Saturn then Some (sprintf "--server %O" x.Server) else None

        [ remoting
          azure
          fulma
          server ]
        |> List.choose id
        |> String.concat " "

let configs =
    [ for azure in [ false; true ] do
      for fulma in [ false; true ] do
      for remoting in [ false; true ] do
      for server in [ Saturn; Giraffe ] do
      yield
          { Azure = azure
            Fulma = fulma
            Server = server
            Remoting = remoting }
    ]

let fullLockFileName build client server =
    sprintf "paket_%O_%O_%O.lock" build client server

Target.create "BuildPaketLockFiles" (fun _ ->
    for config in configs do
        let contents =
            [
                "Content" </> "src" </> "Build" </> sprintf "paket_%O.lock" config.ToBuild
                "Content" </> "src" </> "Client" </> sprintf "paket_%O.lock" config.ToClient
                "Content" </> "src" </> "Server" </> sprintf "paket_%O.lock" config.ToServer
            ]
            |> List.map File.read
            |> Seq.concat

        let lockFileName = fullLockFileName config.ToBuild config.ToClient config.ToServer

        File.writeWithEncoding (Text.UTF8Encoding(false)) false ("Content" </> lockFileName) contents
)

Target.create "GenJsonConditions" (fun _ ->
    for config in configs do //TODO this combination is different?
        let lockFileName = fullLockFileName config.ToBuild config.ToClient config.ToServer
        let server = "saturn"
        let deploy = if config.Azure then "azure" else "none"
        let remoting = config.Remoting
        let layoutOperator = if config.Fulma then "!=" else "=="
        let template =
            sprintf """                    {
                        "include": "%s",
                        "condition": "(server == \"%s\" && remoting == %b && deploy == \"%s\" && layout %s \"none\")",
                        "rename": { "%s": "paket.lock" }
                    },"""
                 lockFileName
                 server
                 remoting
                 deploy
                 layoutOperator
                 lockFileName

        printfn "%s" template
)

Target.create "GenPaketLockFiles" (fun _ ->
    let baseDir = "gen-paket-lock-files"
    Directory.delete baseDir
    Directory.create baseDir

    for config in configs do
        let dirName = baseDir </> "tmp"
        Directory.delete dirName
        Directory.create dirName
        let arg = string config

        run "dotnet" (sprintf "new SAFE %s" arg) dirName

        let lockFile = dirName </> "paket.lock"

        if not (File.exists lockFile) then
            printfn "'paket.lock' doesn't exist for args '%s', installing..." arg
            run "mono" ".paket/paket.exe install" dirName

        let lines = File.readAsString lockFile
        Directory.delete dirName
        Directory.create dirName
        let delimeter = "GROUP "
        let groups =
            lines
            |> String.splitStr delimeter
            |> List.filter (String.isNullOrWhiteSpace >> not)
            |> List.map (fun group -> group.Substring(0, group.IndexOf Environment.NewLine), delimeter + group)
        for (groupName, group) in groups do
            let dirName = baseDir </> groupName
            Directory.create dirName
            let lockFileSuffix =
                match groupName with
                | "Build" -> string config.ToBuild
                | "Client" -> string config.ToClient
                | "Server" -> string config.ToServer
                | _ -> failwithf "Unhandled name '%s'" groupName
            let fileName = sprintf "paket_%s.lock" lockFileSuffix
            let filePath = dirName </> fileName
            if not (File.exists filePath) then
                File.writeString false filePath group
                Shell.copyFile ("Content" </> "src" </> groupName </> fileName) (dirName </> fileName)
)

Target.create "Tests" (fun _ ->
    let cmd = "run"
    let args = "--project tests/tests.fsproj"
    let result = DotNet.exec id cmd args
    if not result.OK then failwithf "`dotnet %s %s` failed" cmd args
)

Target.create "Push" (fun _ ->
    Paket.push ( fun args ->
        { args with
                PublishUrl = "https://www.nuget.org"
                WorkingDir = nupkgDir
        }
    )

    let remoteGit = "upstream"
    let commitMsg = sprintf "Bumping version to %O" release.NugetVersion
    let tagName = string release.NugetVersion

    Git.Branches.checkout "" false "master"
    Git.CommandHelper.directRunGitCommand "" "fetch origin" |> ignore
    Git.CommandHelper.directRunGitCommand "" "fetch origin --tags" |> ignore

    Git.Staging.stageAll ""
    Git.Commit.exec "" commitMsg
    Git.Branches.pushBranch "" remoteGit "master"

    Git.Branches.tag "" tagName
    Git.Branches.pushTag "" remoteGit tagName
)

Target.create "Release" ignore

open Fake.Core.TargetOperators

"Clean"
    ==> "Pack"
    ==> "Install"
    ==> "Tests"
    ==> "Push"
    ==> "Release"

"Install"
    ==> "GenPaketLockFiles"

Target.runOrDefault "Pack"