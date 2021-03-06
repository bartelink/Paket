/// Contains NuGet support.
module Paket.Nuget

open System
open System.IO
open System.Net
open Newtonsoft.Json
open Ionic.Zip
open System.Xml
open System.Text.RegularExpressions
open Paket.Logging

let private loadNuGetOData raw =
    let doc = XmlDocument()
    doc.LoadXml raw
    let manager = new XmlNamespaceManager(doc.NameTable)
    manager.AddNamespace("ns", "http://www.w3.org/2005/Atom")
    manager.AddNamespace("d", "http://schemas.microsoft.com/ado/2007/08/dataservices")
    manager.AddNamespace("m", "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata")
    doc,manager

type NugetPackageCache =
    { Dependencies : (string * VersionRange) list
      Name : string
      Url : string}


/// Gets versions of the given package via OData.
let getAllVersionsFromNugetOData (nugetURL, package) = 
    // we cannot cache this
    async { 
        let! raw = sprintf "%s/Packages?$filter=Id eq '%s'" nugetURL package |> getFromUrl
        let doc,manager = loadNuGetOData raw
        return seq { 
                   for node in doc.SelectNodes("//ns:feed/ns:entry/m:properties/d:Version", manager) do
                       yield node.InnerText
               }
    }

/// Gets all versions no. of the given package.
let getAllVersions (nugetURL, package) = 
    // we cannot cache this
    async { 
        let! raw = sprintf "%s/package-versions/%s?includePrerelease=true" nugetURL package |> safeGetFromUrl
        match raw with
        | None -> let! result = getAllVersionsFromNugetOData (nugetURL, package)
                  return result
        | Some data -> 
            try 
                try 
                    let result = JsonConvert.DeserializeObject<string []>(data) |> Array.toSeq
                    return result
                with _ -> let! result = getAllVersionsFromNugetOData (nugetURL, package)
                          return result
            with exn -> 
                failwithf "Could not get data from %s for package %s.%s Message: %s" nugetURL package 
                    Environment.NewLine exn.Message
                return Seq.empty
    }

/// Gets versions of the given package from local Nuget feed.
let getAllVersionsFromLocalPath (localNugetPath, package) =
    async {
        return Directory.EnumerateFiles(localNugetPath,"*.nupkg",SearchOption.AllDirectories)
               |> Seq.choose (fun fileName -> 
                                   let _match = Regex(sprintf @"%s\.(\d.*)\.nupkg" package, RegexOptions.IgnoreCase).Match(fileName)
                                   if _match.Groups.Count > 1 then Some _match.Groups.[1].Value else None)
    }


/// Parses NuGet version ranges.
let parseVersionRange (text:string) = 
    let failParse() = failwithf "unable to parse %s" text

    let parseBound  = function
        | '[' | ']' -> Including
        | '(' | ')' -> Excluding
        | _         -> failParse()

    if  text = null || text = "" || text = "null" then VersionRange.NoRestriction
    elif not <| text.Contains "," then
        if text.StartsWith "[" then Specific(text.Trim([|'['; ']'|]) |> SemVer.parse)
        else Minimum(SemVer.parse text)
    else
        let fromB = parseBound text.[0]
        let toB   = parseBound (Seq.last text)
        let versions = text
                        .Trim([|'['; ']';'(';')'|])
                        .Split([|','|], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.map SemVer.parse
        match versions.Length with
        | 2 ->
            Range(fromB, versions.[0], versions.[1], toB)
        | 1 ->
            if text.[1] = ',' then
                match fromB, toB with
                | Excluding, Including -> Maximum(versions.[0])
                | Excluding, Excluding -> LessThan(versions.[0])
                | _ -> failParse()
            else 
                match fromB, toB with
                | Excluding, Excluding -> GreaterThan(versions.[0])
                | _ -> failParse()
        | _ -> failParse()
            
/// Gets package details from Nuget via OData
let getDetailsFromNugetViaOData nugetURL package version = 
    async { 
        let! raw = sprintf "%s/Packages(Id='%s',Version='%s')" nugetURL package version |> getFromUrl
        let doc,manager = loadNuGetOData raw
            
        let getAttribute name = 
            seq { 
                   for node in doc.SelectNodes(sprintf "//ns:entry/m:properties/d:%s" name, manager) do
                       yield node.InnerText
               }
               |> Seq.head

        let officialName = 
            seq { 
                   for node in doc.SelectNodes("//ns:entry/ns:title", manager) do
                       yield node.InnerText
               }
               |> Seq.head


        let downloadLink = 
            seq { 
                   for node in doc.SelectNodes("//ns:entry/ns:content", manager) do
                       let downloadType = node.Attributes.["type"].Value
                       if downloadType = "application/zip" || downloadType = "binary/octet-stream" then
                           yield node.Attributes.["src"].Value
               }
               |> Seq.head


        let packages = 
            getAttribute "Dependencies"
            |> fun s -> s.Split([| '|' |], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun d -> d.Split ':')
            |> Array.filter (fun d -> Array.isEmpty d
                                      |> not && d.[0] <> "")
            |> Array.map (fun a -> 
                   a.[0], 
                   if a.Length > 1 then a.[1] else "0")
            |> Array.map (fun (name, version) -> name, parseVersionRange version)
            |> Array.toList

        return { Name = officialName; Url = downloadLink; Dependencies = packages }
    }


/// The NuGet cache folder.
let CacheFolder = 
    let appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
    let di = DirectoryInfo(Path.Combine(Path.Combine(appData, "NuGet"), "Cache"))
    if not di.Exists then
        di.Create()
    di.FullName

let private loadFromCacheOrOData force fileName nugetURL package version = 
    async {
        if not force && File.Exists fileName then
            try 
                let json = File.ReadAllText(fileName)
                let cachedObject = JsonConvert.DeserializeObject<NugetPackageCache>(json)                
                if cachedObject.Name = null || cachedObject.Url = null then
                    failwith "invalid cache"
                
                return false,cachedObject
            with _ -> 
                let! details = getDetailsFromNugetViaOData nugetURL package version
                return true,details
        else
            let! details = getDetailsFromNugetViaOData nugetURL package version
            return true,details
    }

/// Tries to get download link and direct dependencies from Nuget
/// Caches calls into json file
let getDetailsFromNuget force nugetURL package version = 
    async {
        try            
            let fi = FileInfo(Path.Combine(CacheFolder,sprintf "%s.%s.json" package version))
            let! (invalidCache,details) = loadFromCacheOrOData force fi.FullName nugetURL package version 
            if invalidCache then
                File.WriteAllText(fi.FullName,JsonConvert.SerializeObject(details))
            return details
        with
        | _ -> return! getDetailsFromNugetViaOData nugetURL package version 
    }    
    
/// Reads direct dependencies from a nupkg file
let getDetailsFromLocalFile path package version =
    async {
        let nupkg = FileInfo(Path.Combine(path, sprintf "%s.%s.nupkg" package version))
        let zip = ZipFile.Read(nupkg.FullName)
        let zippedNuspec = (zip |> Seq.find (fun f -> f.FileName.EndsWith ".nuspec"))

        zippedNuspec.Extract(Path.GetTempPath(), ExtractExistingFileAction.OverwriteSilently)

        let nuspec = FileInfo(Path.Combine(Path.GetTempPath(), zippedNuspec.FileName))
        
        let xmlDoc = XmlDocument()
        nuspec.FullName |> xmlDoc.Load

        let ns = new XmlNamespaceManager(xmlDoc.NameTable);
        ns.AddNamespace("x", "http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd");
        ns.AddNamespace("y", "http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd");
        
        let nsUri = xmlDoc.LastChild.NamespaceURI
        let pfx = ns.LookupPrefix(nsUri)

        let dependencies = 
            xmlDoc.SelectNodes(sprintf "/%s:package/%s:metadata/%s:dependencies/%s:dependency" pfx pfx pfx pfx, ns)
            |> Seq.cast<XmlNode>
            |> Seq.map (fun node -> 
                            let name = node.Attributes.["id"].Value                            
                            let version = 
                                if node.Attributes.["version"] <> null then 
                                    parseVersionRange node.Attributes.["version"].Value 
                                else 
                                    parseVersionRange "0"
                            name,version) 
            |> Seq.toList

        let officialName = 
            xmlDoc.SelectNodes(sprintf "/%s:package/%s:metadata/%s:id" pfx pfx pfx, ns)
            |> Seq.cast<XmlNode>
            |> Seq.head
            |> fun node -> node.InnerText

        File.Delete(nuspec.FullName)

        return { Name = officialName; Url = package; Dependencies = dependencies }
    }

/// Downloads the given package to the NuGet Cache folder
let DownloadPackage(url, name, version, force) = 
    async { 
        let targetFileName = Path.Combine(CacheFolder, name + "." + version + ".nupkg")
        let targetFile = FileInfo targetFileName
        if not force && targetFile.Exists && targetFile.Length > 0L then 
            verbosefn "%s %s already downloaded" name version
            return targetFileName
        else 
            // discover the link on the fly
            let! nugetPackage = getDetailsFromNuget force url name version
            try
                tracefn "Downloading %s %s to %s" name version targetFileName

                let request = HttpWebRequest.Create(Uri nugetPackage.Url) :?> HttpWebRequest
                request.AutomaticDecompression <- DecompressionMethods.GZip ||| DecompressionMethods.Deflate
                use! httpResponse = request.AsyncGetResponse()
            
                use httpResponseStream = httpResponse.GetResponseStream()
            
                let bufferSize = 4096
                let buffer : byte [] = Array.zeroCreate bufferSize
                let bytesRead = ref -1

                use fileStream = File.Create(targetFileName)
            
                while !bytesRead <> 0 do
                    let! bytes = httpResponseStream.AsyncRead(buffer, 0, bufferSize)
                    bytesRead := bytes
                    do! fileStream.AsyncWrite(buffer, 0, !bytesRead)
                return targetFileName
            with
            | exn -> failwithf "Could not download %s %s.%s    %s" name version Environment.NewLine exn.Message
                     return targetFileName
    }


/// Extracts the given package to the ./packages folder
let ExtractPackage(fileName, name, version, force) = 
    async { 
        let targetFolder = DirectoryInfo(Path.Combine("packages", name)).FullName
        let fi = FileInfo(fileName)
        let targetFile = FileInfo(Path.Combine(targetFolder, fi.Name))
        if not force && targetFile.Exists then           
            verbosefn "%s %s already extracted" name version
            return targetFolder
        else 
            CleanDir targetFolder
            File.Copy(fileName, targetFile.FullName)
            let zip = ZipFile.Read(fileName)
            Directory.CreateDirectory(targetFolder) |> ignore
            for e in zip do
                e.Extract(targetFolder, ExtractExistingFileAction.OverwriteSilently)

            // cleanup folder structure
            let rec cleanup (dir : DirectoryInfo) = 
                for sub in dir.GetDirectories() do
                    let newName = sub.FullName.Replace("%2B", "+")
                    if sub.FullName <> newName then 
                        Directory.Move(sub.FullName, newName)
                        cleanup (DirectoryInfo newName)
                    else
                        cleanup sub
            cleanup (DirectoryInfo targetFolder)
            tracefn "%s %s unzipped to %s" name version targetFolder
            return targetFolder
    }

/// Finds all libraries in a nuget packge.
let GetLibraries(targetFolder) =
    let dir = DirectoryInfo(Path.Combine(targetFolder,"lib"))
    let libs = 
        if dir.Exists then
            dir.GetFiles("*.dll",SearchOption.AllDirectories)
        else
            Array.empty

    if Logging.verbose then
        if Array.isEmpty libs then 
            verbosefn "No libraries found in %s" targetFolder 
        else
            let s = String.Join(Environment.NewLine + "  - ",libs |> Array.map (fun l -> l.FullName))
            verbosefn "Libraries found in %s:%s" targetFolder s

    libs

/// Lists packages defined in a NuGet packages.config
let ReadPackagesConfig(configFile : FileInfo) =
    let doc = XmlDocument()
    doc.Load configFile.FullName
    { File = configFile
      Type = if configFile.Directory.Name = ".nuget" then SolutionLevel else ProjectLevel
      Packages = [for node in doc.SelectNodes("//package") ->
                      node.Attributes.["id"].Value, node.Attributes.["version"].Value |> SemVer.parse ]}


//TODO: Should we really be able to call these methods with invalid arguments?
let GetPackageDetails force sources package version = 
    let rec tryNext xs = 
        match xs with
        | source :: rest -> 
            try 
                match source with
                | Nuget url -> 
                    getDetailsFromNuget force url package version |> Async.RunSynchronously
                | LocalNuget path -> 
                    getDetailsFromLocalFile path package version |> Async.RunSynchronously
                |> fun x -> source,x
            with _ -> tryNext rest
        | [] -> failwithf "Couldn't get package details for package %s on %A" package sources
    
    let source, nugetObject = tryNext sources
    { Name = nugetObject.Name
      Source = source
      DownloadLink = nugetObject.Url
      DirectDependencies = nugetObject.Dependencies  }

let GetVersions sources package = 
    sources
    |> Seq.map (fun source -> 
           match source with
           | Nuget url -> getAllVersions (url, package)
           | LocalNuget path -> getAllVersionsFromLocalPath (path, package))
    |> Async.Parallel
    |> Async.RunSynchronously
    |> Seq.concat
    |> Seq.toList
    |> List.map SemVer.parse