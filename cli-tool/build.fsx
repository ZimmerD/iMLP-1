#r "paket:
nuget BlackFox.Fake.BuildTask
nuget Fake.Core.Target
nuget Fake.Core.Process
nuget Fake.Core.ReleaseNotes
nuget Fake.IO.FileSystem
nuget Fake.DotNet.Cli
nuget Fake.DotNet.MSBuild
nuget Fake.DotNet.AssemblyInfoFile
nuget Fake.DotNet.Paket
nuget Fake.DotNet.FSFormatting
nuget Fake.DotNet.Fsi
nuget Fake.DotNet.NuGet
nuget Fake.Api.Github
nuget Fake.DotNet.Testing.Expecto 
nuget Fake.Tools.Git //"

#if !FAKE
#load "./.fake/build.fsx/intellisense.fsx"
#r "netstandard" // Temp fix for https://github.com/dotnet/fsharp/issues/5216
#endif

open BlackFox.Fake
open System.IO
open Fake.Core
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Tools
open Fake.Api

[<AutoOpen>]
/// user interaction prompts for critical build tasks where you may want to interrupt when you see wrong inputs.
module MessagePrompts =

    let prompt (msg:string) =
        System.Console.Write(msg)
        System.Console.ReadLine().Trim()
        |> function | "" -> None | s -> Some s
        |> Option.map (fun s -> s.Replace ("\"","\\\""))

    let rec promptYesNo msg =
        match prompt (sprintf "%s [Yn]: " msg) with
        | Some "Y" | Some "y" -> true
        | Some "N" | Some "n" -> false
        | _ -> System.Console.WriteLine("Sorry, invalid answer"); promptYesNo msg

    let releaseMsg = """This will stage all uncommitted changes, push them to the origin and bump the release version to the latest number in the RELEASE_NOTES.md file. 
        Do you want to continue?"""

    let releaseDocsMsg = """This will push the docs to gh-pages. Remember building the docs prior to this. Do you want to continue?"""

/// Executes a dotnet command in the given working directory
let runDotNet cmd workingDir =
    let result =
        DotNet.exec (DotNet.Options.withWorkingDirectory workingDir) cmd ""
    if result.ExitCode <> 0 then failwithf "'dotnet %s' failed in %s" cmd workingDir

/// Metadata about the project
module ProjectInfo = 

    let project = "iMLP"

    let testProject = "tests/iMLP.Tests/iMLP.Tests.fsproj"

    let summary = "A F# interactive charting library using plotly.js"

    let solutionFile  = "iMLP.sln"

    let configuration = "Release"

    // Git configuration (used for publishing documentation in gh-pages branch)
    // The profile where the project is posted
    let gitOwner = "CSBiology"
    let gitHome = sprintf "%s/%s" "https://github.com" gitOwner

    let gitName = "iMLP"

    let website = "/iMLP"

    let pkgDir = "pkg"

    let publishDir = "publish"

    let release = ReleaseNotes.load "RELEASE_NOTES.md"

    let projectRepo = "https://github.com/plotly/Plotly.NET"

    let stableVersion = SemVer.parse release.NugetVersion

    let stableVersionTag = (sprintf "%i.%i.%i" stableVersion.Major stableVersion.Minor stableVersion.Patch )

    let mutable prereleaseSuffix = ""

    let mutable prereleaseTag = ""

    let mutable isPrerelease = false


/// Barebones, minimal build tasks
module BasicTasks = 

    open ProjectInfo

    let setPrereleaseTag = BuildTask.create "SetPrereleaseTag" [] {
        printfn "Please enter pre-release package suffix"
        let suffix = System.Console.ReadLine()
        prereleaseSuffix <- suffix
        prereleaseTag <- (sprintf "%s-%s" release.NugetVersion suffix)
        isPrerelease <- true
    }

    let clean = BuildTask.create "Clean" [] {
        !! "src/**/bin"
        ++ "src/**/obj"
        ++ pkgDir
        ++ publishDir
        ++ "bin"
        |> Shell.cleanDirs 
    }

    let build = BuildTask.create "Build" [clean] {
        solutionFile
        |> DotNet.build (fun buildParams ->
            let standardParams = Fake.DotNet.MSBuild.CliArguments.Create ()
            {
                buildParams with
                    Configuration = DotNet.BuildConfiguration.fromString configuration
                    MSBuildParams = {
                        standardParams with
                            Properties = [
                                "Platform","x64"
                            ]
                    };
            }
        )
    }

    let publishBinariesWin = BuildTask.create "PublishBinariesWin" [clean.IfNeeded; build.IfNeeded] {
        solutionFile
        |> DotNet.publish (fun p ->
            let standardParams = Fake.DotNet.MSBuild.CliArguments.Create ()
            {
                p with
                    Runtime = Some "win-x64"
                    Configuration = DotNet.BuildConfiguration.fromString configuration
                    OutputPath = Some (sprintf "%s/win-x64" publishDir)
                    MSBuildParams = {
                        standardParams with
                            Properties = [
                                "Platform","x64"
                            ]
                    };
            }
        )
    }

    let publishBinariesLinux = BuildTask.create "PublishBinariesLinux" [clean.IfNeeded; build.IfNeeded] {
        solutionFile
        |> DotNet.publish (fun p ->
            let standardParams = Fake.DotNet.MSBuild.CliArguments.Create ()
            {
                p with
                    Runtime = Some "linux-x64"
                    Configuration = DotNet.BuildConfiguration.fromString configuration
                    OutputPath = Some (sprintf "%s/linux-x64" publishDir)
                    MSBuildParams = {
                        standardParams with
                            Properties = [
                                "Platform","x64"
                            ]
                    }
            }
        )
    }

    let publishBinaries = BuildTask.createEmpty "PublishBinaries" [clean; build; publishBinariesWin; publishBinariesLinux]

    let copyBinaries = BuildTask.create "CopyBinaries" [clean; build] {
        let targets = 
            !! "src/**/*.??proj"
            -- "src/**/*.shproj"
            |>  Seq.map (fun f -> ((Path.getDirectory f) </> "bin/x64" </> configuration, "bin" </> (Path.GetFileNameWithoutExtension f)))
        for i in targets do printfn "%A" i
        targets
        |>  Seq.iter (fun (fromDir, toDir) -> Shell.copyDir toDir fromDir (fun _ -> true))
    }

/// Test executing build tasks
module TestTasks = 

    open ProjectInfo
    open BasicTasks

    let runTests = BuildTask.create "RunTests" [clean; build; copyBinaries] {
        let standardParams = Fake.DotNet.MSBuild.CliArguments.Create ()
        Fake.DotNet.DotNet.test(fun testParams ->
            {
                testParams with
                    Logger = Some "console;verbosity=detailed"
                    Configuration = DotNet.BuildConfiguration.fromString configuration
                    NoBuild = true
            }
        ) testProject
    }

    // to do: use this once we have actual tests
    let runTestsWithCodeCov = BuildTask.create "RunTestsWithCodeCov" [clean; build; copyBinaries] {
        let standardParams = Fake.DotNet.MSBuild.CliArguments.Create ()
        Fake.DotNet.DotNet.test(fun testParams ->
            {
                testParams with
                    MSBuildParams = {
                        standardParams with
                            Properties = [
                                "AltCover","true"
                                "AltCoverCobertura","../../codeCov.xml"
                                "AltCoverForce","true"
                            ]
                    };
                    Logger = Some "console;verbosity=detailed"
                    Configuration = DotNet.BuildConfiguration.fromString configuration
                    NoBuild = true
            }
        ) testProject
}
/// Package creation
module PackageTasks = 

    open ProjectInfo

    open BasicTasks
    open TestTasks

    let pack = BuildTask.create "Pack" [clean; build; runTests; copyBinaries] {
        if promptYesNo (sprintf "creating stable package with version %s OK?" stableVersionTag ) 
            then
                !! "src/**/*.*proj"
                |> Seq.iter (Fake.DotNet.DotNet.pack (fun p ->
                    let msBuildParams =
                        {p.MSBuildParams with 
                            Properties = ([
                                "Version",stableVersionTag
                                "PackageReleaseNotes",  (release.Notes |> String.concat "\r\n")
                                "Platform","x64"
                            ] @ p.MSBuildParams.Properties)
                        }
                    {
                        p with 
                            MSBuildParams = msBuildParams
                            OutputPath = Some pkgDir
                            Configuration = DotNet.BuildConfiguration.fromString configuration
                    }
                ))
        else failwith "aborted"
    }

    let packPrerelease = BuildTask.create "PackPrerelease" [setPrereleaseTag; clean; build; runTests; copyBinaries] {
        if promptYesNo (sprintf "package tag will be %s OK?" prereleaseTag )
            then 
                !! "src/**/*.*proj"
                //-- "src/**/Plotly.NET.Interactive.fsproj"
                |> Seq.iter (Fake.DotNet.DotNet.pack (fun p ->
                            let msBuildParams =
                                {p.MSBuildParams with 
                                    Properties = ([
                                        "Version", prereleaseTag
                                        "PackageReleaseNotes",  (release.Notes |> String.toLines )
                                        "Platform","x64"
                                    ] @ p.MSBuildParams.Properties)
                                }
                            {
                                p with 
                                    VersionSuffix = Some prereleaseSuffix
                                    OutputPath = Some pkgDir
                                    MSBuildParams = msBuildParams
                                    Configuration = DotNet.BuildConfiguration.fromString configuration
                            }
                ))
        else
            failwith "aborted"
    }

/// Buildtasks that release stuff, e.g. packages, git tags, documentation, etc.
module ReleaseTasks =

    open ProjectInfo

    open BasicTasks
    open TestTasks
    open PackageTasks

    let createTag = BuildTask.create "CreateTag" [clean; build; copyBinaries; runTests; pack] {
        if promptYesNo (sprintf "tagging branch with %s OK?" stableVersionTag ) then
            Git.Branches.tag "" stableVersionTag
            Git.Branches.pushTag "" projectRepo stableVersionTag
        else
            failwith "aborted"
    }

    let createPrereleaseTag = BuildTask.create "CreatePrereleaseTag" [setPrereleaseTag; clean; build; copyBinaries; runTests; packPrerelease] {
        if promptYesNo (sprintf "tagging branch with %s OK?" prereleaseTag ) then 
            Git.Branches.tag "" prereleaseTag
            Git.Branches.pushTag "" projectRepo prereleaseTag
        else
            failwith "aborted"
    }

    
    let publishNuget = BuildTask.create "PublishNuget" [clean; build; copyBinaries; runTests; pack] {
        let targets = (!! (sprintf "%s/*.*pkg" pkgDir ))
        for target in targets do printfn "%A" target
        let msg = sprintf "release package with version %s?" stableVersionTag
        if promptYesNo msg then
            let source = "https://api.nuget.org/v3/index.json"
            let apikey =  Environment.environVar "NUGET_KEY"
            for artifact in targets do
                let result = DotNet.exec id "nuget" (sprintf "push -s %s -k %s %s --skip-duplicate" source apikey artifact)
                if not result.OK then failwith "failed to push packages"
        else failwith "aborted"
    }

    let publishNugetPrerelease = BuildTask.create "PublishNugetPrerelease" [clean; build; copyBinaries; runTests; packPrerelease] {
        let targets = (!! (sprintf "%s/*.*pkg" pkgDir ))
        for target in targets do printfn "%A" target
        let msg = sprintf "release package with version %s?" prereleaseTag 
        if promptYesNo msg then
            let source = "https://api.nuget.org/v3/index.json"
            let apikey =  Environment.environVar "NUGET_KEY"
            for artifact in targets do
                let result = DotNet.exec id "nuget" (sprintf "push -s %s -k %s %s --skip-duplicate" source apikey artifact)
                if not result.OK then failwith "failed to push packages"
        else failwith "aborted"
    }

module ToolTasks =

    open ProjectInfo
    open BasicTasks
    open TestTasks
    open PackageTasks

    let installPackagedTool = BuildTask.create "InstallPackagedTool" [packPrerelease] {
        Directory.ensure "tests/tool-tests"
        runDotNet "new tool-manifest --force" "tests/tool-tests"
        runDotNet (sprintf "tool install --add-source ../../%s imlp --version %s" pkgDir prereleaseTag) "tests/tool-tests"
    }

    let testPackagedTool = BuildTask.create "TestPackagedTool" [installPackagedTool] {
        runDotNet "imlp --help" "tests/tool-tests"
    }

open BasicTasks
open TestTasks
open PackageTasks
open ReleaseTasks

/// Full release of nuget package, git tag, and documentation for the stable version.
let _release = 
    BuildTask.createEmpty 
        "Release" 
        [clean; build; copyBinaries; runTests; pack; createTag; publishNuget ]

/// Full release of nuget package, git tag, and documentation for the prerelease version.
let _preRelease = 
    BuildTask.createEmpty 
        "PreRelease" 
        [setPrereleaseTag; clean; build; copyBinaries; runTests; packPrerelease; createPrereleaseTag; publishNugetPrerelease]

// run copyBinaries by default
BuildTask.runOrDefault copyBinaries