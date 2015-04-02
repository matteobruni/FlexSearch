﻿// ----------------------------------------------------------------------------
// (c) Seemant Rajvanshi, 2013
//
// This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
// copy of the license can be found in the License.txt file at the root of this distribution. 
// By using this source code in any fashion, you are agreeing to be bound 
// by the terms of the Apache License, Version 2.0.
//
// You must not remove this notice, or any other, from this software.
// ----------------------------------------------------------------------------
namespace FlexSearch.Core

open FlexLucene.Analysis
open FlexLucene.Search
open FlexSearch.Core
open Microsoft.Owin
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.ComponentModel.Composition
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks.Dataflow
open java.io
open java.util



/// <summary>
/// General key value based settings store used across Flex to store all settings
/// Do not use this as a cache store
/// </summary>
type IPersistanceStore = 
    abstract Get<'T when 'T : equality> : key:string -> Choice<'T, Error>
    abstract GetAll<'T> : unit -> IEnumerable<'T>
    abstract Put<'T> : key:string * value:'T -> Choice<unit, Error>
    abstract Delete<'T> : key:string -> Choice<unit, Error>
    abstract DeleteAll<'T> : unit -> Choice<unit, Error>



/// <summary>
/// General factory Interface for all MEF based factories
/// </summary>
type IFlexFactory<'T> = 
    abstract GetModuleByName : string -> Choice<'T, Error>
    abstract ModuleExists : string -> bool
    abstract GetAllModules : unit -> Dictionary<string, 'T>
    abstract GetMetaData : string -> Choice<IDictionary<string, obj>, Error>

/// <summary>
/// The meta data interface which is used to read MEF based
/// meta data properties 
/// </summary>
type IFlexMetaData = 
    abstract Name : string

/// <summary>
/// Flex Index validator interface
/// This will validate all index settings. This could be easily replaced by 
/// a higher order function but it makes C# to F# interoperability a bit 
/// difficult
/// </summary>
//type IIndexValidator = 
//    abstract Validate : Index.T -> Choice<unit, Error>



/// <summary>
/// Search service interface
/// </summary>
type ISearchService = 
    abstract Search : SearchQuery.Dto -> Choice<SearchResults, Error>
    abstract SearchAsDocmentSeq : SearchQuery.Dto -> Choice<seq<Document.Dto> * int * int, Error>
    abstract SearchAsDictionarySeq : SearchQuery.Dto -> Choice<seq<Dictionary<string, string>> * int * int, Error>
    abstract SearchUsingProfile : query:SearchQuery.Dto * inputFields:Dictionary<string, string>
     -> Choice<SearchResults, Error>




/// <summary>
/// Queuing related operations
/// </summary>
type IQueueService = 
    abstract AddDocumentQueue : document:Document.Dto -> unit
    abstract AddOrUpdateDocumentQueue : document:Document.Dto -> unit


/// <summary>
/// Generic job service interface
/// </summary>
//type IJobService = 
//    abstract GetJob : string -> Choice<Job, Error>
//    abstract DeleteAllJobs : unit -> Choice<unit, Error>
//    abstract UpdateJob : Job -> Choice<unit, Error>

/// <summary>
/// General Interface to offload all resource loading responsibilities. This will
/// be used to parse settings, load text files etc.
/// </summary> 
//type IResourceService = 
//    abstract GetResource<'T> : resourceName:string -> Choice<'T, Error>
//    abstract UpdateResource<'T> : resourceName:string * resource:'T -> Choice<unit, Error>
//    abstract DeleteResource<'T> : resourceName:string -> Choice<unit, Error>

/// <summary>
///  Analyzer/Analysis related services
/// </summary>
//type IAnalyzerService = 
//    abstract GetAnalyzer : analyzerName:string -> Choice<Analyzer, Error>
//    abstract GetAnalyzerInfo : analyzerName:string -> Choice<FlexSearch.Core.Analyzer, Error>
//    abstract DeleteAnalyzer : analyzerName:string -> Choice<unit, Error>
//    abstract AddOrUpdateAnalyzer : analyzer:FlexSearch.Core.Analyzer -> Choice<unit, Error>
//    abstract GetAllAnalyzers : unit -> Choice<List<FlexSearch.Core.Analyzer>, Error>
//    abstract Analyze : analyzerName:string * input:string -> Choice<string, Error>

/// <summary>
/// Interface which exposes all top level factories
/// Could have exposed all these through a simple dictionary over IFlexFactory
/// but then we would have to perform a look up to get each factory instance.
/// This is fairly easy to manage as all the logic is in IFlexFactory.
/// Also reduces passing of parameters.
/// </summary>
//type IFactoryCollection = 
//    abstract FilterFactory : IFlexFactory<IFlexFilterFactory>
//    abstract TokenizerFactory : IFlexFactory<IFlexTokenizerFactory>
//    abstract AnalyzerFactory : IFlexFactory<Analyzer>
//    abstract SearchQueryFactory : IFlexFactory<IFlexQuery>
///// <summary>
///// Import handler interface to support bulk indexing
///// </summary>
//type IImportHandler = 
//    abstract SupportsBulkIndexing : unit -> bool
//    abstract SupportsIncrementalIndexing : unit -> bool
//    abstract ProcessBulkRequest : string * ImportRequest -> unit
//    abstract ProcessIncrementalRequest : string * ImportRequest -> Choice<unit, Error>

//type IThreadSafeWriter = 
//    abstract WriteFile<'T> : filePath:string * content:'T -> Choice<unit, OperationMessage>
//    abstract ReadFile<'T> : filePath:string -> Choice<'T, OperationMessage>
//    abstract DeleteFile : filePath:string -> Choice<unit, OperationMessage>
//
///// <summary>
///// Thread safe file writer. Create one per folder and call it for writing to specific files
///// in that folder from multiple threads. Uses one lock per folder.
///// Note : This is not meant to be used for huge files and should be used for writing configuration
///// files.
///// </summary>
//[<Sealed>]
//type ThreadSafeFileWiter(formatter : IFormatter) = 
//    let GetPathWithExtension(path) =
//        if Path.GetExtension(path) <> Constants.SettingsFileExtension then
//            path + Constants.SettingsFileExtension
//        else
//            path
//    interface IThreadSafeWriter with
//        member this.DeleteFile(filePath : string) : Choice<unit, OperationMessage> = 
//            let path = GetPathWithExtension(filePath)
//            if File.Exists(path) then 
//                use mutex = new Mutex(false, path.Replace("\\", ""))
//                File.Delete(path)
//                Choice1Of2()
//            else 
//                // Don't care if file is no longer present
//                Choice1Of2()
//        
//        member this.ReadFile(filePath : string) : Choice<'T, OperationMessage> = 
//            let path = GetPathWithExtension(filePath)
//            if File.Exists(path) then 
//                try 
//                    use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
//                    let response = formatter.DeSerialize<'T>(stream)
//                    Choice1Of2(response)
//                with e -> 
//                    Choice2Of2(Errors.FILE_NOT_FOUND
//                               |> GenerateOperationMessage
//                               |> Append("filepath", path)
//                               |> Append("exception", e.Message))
//            else 
//                Choice2Of2(Errors.FILE_NOT_FOUND
//                           |> GenerateOperationMessage
//                           |> Append("filepath", path))
//        
//        member this.WriteFile<'T>(filePath : string, content : 'T) = 
//            let path = GetPathWithExtension(filePath)
//            use mutex = new Mutex(true, path.Replace("\\", ""))
//            Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
//            try 
//                mutex.WaitOne(-1) |> ignore
//                use file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read)
//                let byteContent = System.Text.UTF8Encoding.UTF8.GetBytes(formatter.SerializeToString(content))
//                file.Write(byteContent, 0, byteContent.Length)
//                mutex.ReleaseMutex()
//                Choice1Of2()
//            with e -> 
//                mutex.ReleaseMutex()
//                Choice2Of2(Errors.FILE_WRITE_ERROR
//                           |> GenerateOperationMessage
//                           |> Append("filepath", path)
//                           |> Append("exception", e.Message))