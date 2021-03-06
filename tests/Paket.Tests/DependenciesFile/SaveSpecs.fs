module paket.dependenciesFile.SaveSpecs

open Paket
open NUnit.Framework
open FsUnit
open TestHelpers

let config1 = """source http://nuget.org/api/v2

nuget Castle.Windsor-log4net ~> 3.2
nuget Rx-Main ~> 2.0
nuget FAKE 1.1
nuget SignalR 3.3.2"""

[<Test>]
let ``should serialize simple config``() = 
    let cfg = DependenciesFile.FromCode(noSha1,config1)
    
    cfg.ToString()
    |> shouldEqual (normalizeLineEndings config1)


let strictConfig = """references strict
source http://nuget.org/api/v2

nuget FAKE ~> 3.0"""

[<Test>]
let ``should serialize strict config``() = 
    let cfg = DependenciesFile.FromCode(noSha1,strictConfig)
    
    cfg.ToString()
    |> shouldEqual (normalizeLineEndings strictConfig)


let simplestConfig = """nuget FAKE ~> 3.0"""

[<Test>]
let ``should serialize simplestConfig``() = 
    let cfg = DependenciesFile.FromCode(noSha1,simplestConfig)
    
    cfg.ToString()
    |> shouldEqual (normalizeLineEndings simplestConfig)



let ownConfig = """source http://nuget.org/api/v2

nuget Octokit
nuget Newtonsoft.Json
nuget UnionArgParser
nuget NUnit.Runners >= 2.6
nuget NUnit >= 2.6
nuget FAKE
nuget FSharp.Formatting
nuget DotNetZip ~> 1.9.3
nuget SourceLink.Fake
nuget NuGet.CommandLine

github forki/FsUnit FsUnit.fs"""

[<Test>]
let ``should serialize packet's own config``() = 
    let cfg = DependenciesFile.FromCode(fakeSha1,ownConfig)
    
    cfg.ToString()
    |> shouldEqual (normalizeLineEndings ownConfig)


let configWithRemoteFile = """github fsharp/FAKE:master src/app/FAKE/Cli.fs
github fsharp/FAKE:bla123zxc src/app/FAKE/FileWithCommit.fs"""

[<Test>]
let ``should serialize remote files in config``() = 
    let cfg = DependenciesFile.FromCode(noSha1,configWithRemoteFile)
    
    cfg.ToString()
    |> shouldEqual (normalizeLineEndings configWithRemoteFile)


let allVersionsConfig = """source http://nuget.org/api/v2

nuget Example > 1.2.3
nuget Example2 <= 1.2.3
nuget Example3 < 2.2.3
nuget Example4 >= 1.2.3 < 1.5"""

[<Test>]
let ``should serialize config with all kinds of versions``() = 
    let cfg = DependenciesFile.FromCode(noSha1,allVersionsConfig)
    
    cfg.ToString()
    |> shouldEqual (normalizeLineEndings allVersionsConfig)