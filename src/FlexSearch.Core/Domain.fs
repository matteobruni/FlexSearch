﻿// ----------------------------------------------------------------------------
//  Licensed to FlexSearch under one or more contributor license 
//  agreements. See the NOTICE file distributed with this work 
//  for additional information regarding copyright ownership. 
//
//  This source code is subject to terms and conditions of the 
//  Apache License, Version 2.0. A copy of the license can be 
//  found in the License.txt file at the root of this distribution. 
//  You may also obtain a copy of the License at:
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
//  By using this source code in any fashion, you are agreeing
//  to be bound by the terms of the Apache License, Version 2.0.
//
//  You must not remove this notice, or any other, from this software.
// ----------------------------------------------------------------------------
namespace FlexSearch.Core

open FlexLucene.Analysis
open FlexLucene.Analysis.Core
open FlexLucene.Analysis.Custom
open FlexLucene.Analysis.Miscellaneous
open FlexLucene.Analysis.Util
open FlexLucene.Codecs
open FlexLucene.Codecs.Lucene42
open FlexLucene.Document
open FlexLucene.Index
open FlexLucene.Search
open FlexLucene.Search.Similarities
open FlexLucene.Store
open Microsoft.FSharp.Reflection
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.ComponentModel.Composition
open System.IO
open System.Linq
open System.Reflection
open System.Text.RegularExpressions
open System.Threading
open java.io
open java.util

//////////////////////////////////////////////////////////////////////////
/// Enums Section
//////////////////////////////////////////////////////////////////////////
[<RequireQualifiedAccessAttribute>]
module FieldSimilarity = 
    open FlexLucene.Search.Similarities
    
    /// Similarity defines the components of Lucene scoring. Similarity 
    /// determines how Lucene weights terms, and Lucene interacts with 
    /// Similarity at both index-time and query-time.
    type T = 
        | Undefined = 0
        /// <summary>
        /// BM25 Similarity defines the components of Lucene scoring.
        /// </summary>
        | BM25 = 1
        /// <summary>
        /// TFIDF Similarity defines the components of Lucene scoring. 
        /// This is the default Lucene similarity.
        /// </summary>
        | TFIDF = 2
    
    /// Converts the enum similarity to Lucene Similarity
    let getLuceneT = 
        function 
        | T.TFIDF -> ok (new DefaultSimilarity() :> Similarity)
        | T.BM25 -> ok (new BM25Similarity() :> Similarity)
        | unknown -> fail (UnSupportedSimilarity(unknown.ToString()))
    
    /// Default similarity provider used by FlexSearch
    [<SealedAttribute>]
    type Provider(mappings : IReadOnlyDictionary<string, Similarity>, defaultFormat : Similarity) = 
        inherit PerFieldSimilarityWrapper()
        override __.get (fieldName) = 
            match mappings.TryGetValue(fieldName) with
            | true, format -> format
            | _ -> defaultFormat

[<RequireQualifiedAccessAttribute>]
module FieldPostingsFormat = 
    /// A postings format is responsible for encoding/decoding terms, postings, and proximity data.
    type T = 
        | Undefined = 0
        /// Wraps Lucene41PostingsFormat format for on-disk storage, but then at read time 
        /// loads and stores all terms and postings directly in RAM as byte[], int[].
        ///
        /// WARNING: This is exceptionally RAM intensive: it makes no effort to compress 
        /// the postings data, storing terms as separate byte[] and postings as 
        /// separate int[], but as a result it gives substantial increase in search performance.
        | Direct = 1
        /// Stores terms and postings (docs, positions, payloads) in RAM, using an FST.
        /// Note that this codec implements advance as a linear scan! This means if you 
        /// store large fields in here, queries that rely on advance will 
        /// (AND BooleanQuery, PhraseQuery) will be relatively slow!
        | Memory = 2
        /// A PostingsFormat useful for low doc-frequency fields such as primary keys. 
        /// Bloom filters are maintained in a ".blm" file which offers "fast-fail" for 
        /// reads in segments known to have no record of the key. A choice of delegate 
        /// PostingsFormat is used to record all other Postings data.
        ///
        /// A choice of BloomFilterFactory can be passed to tailor Bloom Filter settings 
        /// on a per-field basis. The default configuration is DefaultBloomFilterFactory 
        /// which allocates a ~8mb bit-set and hashes values using MurmurHash2. 
        /// This should be suitable for most purposes.
        ///
        /// NOTE: This uses Lucene 4.1 postings format as a wrapper.
        | Bloom_4_1 = 3
        /// This postings format "in-lines" the postings for terms that have low docFreq. 
        /// It wraps another postings format, which is used for writing the non in-lined terms. 
        ///
        /// NOTE: This uses Lucene 4.1 postings format as a wrapper.
        | Pulsing_4_1 = 4
        /// Lucene 4.1 postings format, which encodes postings in packed integer blocks 
        /// for fast decode.
        | Lucene_4_1 = 5
        /// Lucene 5.0 postings format, which encodes postings in packed integer blocks 
        /// for fast decode.
        | Lucene_5_0 = 6
        /// Bloom postings format which uses Lucene 5.0 postings format as wrapper.
        | Bloom_5_0 = 7

[<RequireQualifiedAccessAttribute>]
module DirectoryType = 
    open FlexLucene.Store
    
    /// A Directory is a flat list of files. Files may be written once, when they are created. 
    /// Once a file is created it may only be opened for read, or deleted. Random access is 
    /// permitted both when reading and writing.
    type T = 
        | Undefined = 0
        /// FileSystem Directory is a straightforward implementation using java.io.RandomAccessFile. 
        /// However, it has poor concurrent performance (multiple threads will bottleneck) 
        /// as it synchronizes when multiple threads read from the same file.
        | FileSystem = 1
        /// File-based Directory implementation that uses memory map for reading, and 
        /// FSDirectory.FSIndexOutput for writing.
        /// 
        /// NOTE: memory mapping uses up a portion of the virtual memory address space 
        /// in your process equal to the size of the file being mapped. Before using this 
        /// class, be sure your have plenty of virtual address space, e.g. by using a 64 
        /// bit JRE, or a 32 bit JRE with indexes that are guaranteed to fit within the 
        /// address space. On 32 bit platforms also consult MMapDirectory(File, LockFactory, 
        /// int) if you have problems with mmap failing because of fragmented address 
        /// space. If you get an OutOfMemoryException, it is recommended to reduce the 
        /// chunk size, until it works.
        /// 
        /// Due to this bug in Sun's JRE, MMapDirectory's IndexInput.close() is unable 
        /// to close the underlying OS file handle. Only when GC finally collects the 
        /// underlying objects, which could be quite some time later, will the file handle
        /// be closed.
        /// 
        /// This will consume additional transient disk usage: on Windows, attempts to 
        /// delete or overwrite the files will result in an exception; on other platforms, 
        /// which typically have a "delete on last close" semantics, while such operations
        /// will succeed, the bytes are still consuming space on disk. For many 
        /// applications this limitation is not a problem (e.g. if you have plenty of 
        /// disk space, and you don't rely on overwriting files on Windows) but it's 
        /// still an important limitation to be aware of.
        | MemoryMapped = 2
        /// A memory-resident Directory implementation. This is not intended to work with 
        /// huge indexes. Everything beyond several hundred megabytes will waste resources 
        /// (GC cycles), because it uses an internal buffer size of 1024 bytes, producing 
        /// millions of byte[1024] arrays. This class is optimized for small memory-resident 
        /// indexes. It also has bad concurrency on multi-threaded environments.
        /// 
        /// It is recommended to materialize large indexes on disk and use MMapDirectory, 
        /// which is a high-performance directory implementation working directly on the 
        /// file system cache of the operating system.
        | Ram = 3
    
    /// Create a index directory from the given directory type    
    let getIndexDirectory (directoryType : T, path : string) = 
        // Note: Might move to SingleInstanceLockFactory to provide other services to open
        // the index in read-only mode
        let lockFactory = NativeFSLockFactory.GetDefault()
        let file = (new java.io.File(path)).toPath()
        try 
            match directoryType with
            | T.FileSystem -> ok (FSDirectory.Open(file, lockFactory) :> Directory)
            | T.MemoryMapped -> ok (MMapDirectory.Open(file, lockFactory) :> Directory)
            | T.Ram -> ok (new RAMDirectory() :> Directory)
            | unknown -> fail (UnsupportedDirectoryType(unknown.ToString()))
        with e -> fail (ErrorOpeningIndexWriter(path, exceptionPrinter (e), new ResizeArray<_>()))

/// These options instruct FlexSearch to maintain full term vectors for each document, 
/// optionally including the position and offset information for each term occurrence 
/// in those vectors. These can be used to accelerate highlighting and other ancillary 
/// functionality, but impose a substantial cost in terms of index size. These can 
/// only be configured for custom field type.
type FieldTermVector = 
    | Undefined = 0
    /// Do not store term vectors.
    | DoNotStoreTermVector = 1
    /// Store the term vectors of each document. A term vector is a list of the 
    /// document's terms and their number of occurrences in that document.
    | StoreTermVector = 2
    /// Store the term vector and token position information
    | StoreTermVectorsWithPositions = 3
    /// Store the term vector, Token position and offset information
    | StoreTermVectorsWithPositionsandOffsets = 4

/// Controls how much information is stored in the postings lists.
type FieldIndexOptions = 
    | Undefined = 0
    /// Only documents are indexed: term frequencies and positions are omitted.
    | DocsOnly = 1
    /// Only documents and term frequencies are indexed: positions are omitted.
    | DocsAndFreqs = 2
    /// Indexes documents, frequencies and positions
    | DocsAndFreqsAndPositions = 3
    /// Indexes documents, frequencies, positions and offsets.
    | DocsAndFreqsAndPositionsAndOffsets = 4

[<RequireQualifiedAccessAttribute>]
module IndexVersion = 
    open FlexLucene.Codecs
    open FlexLucene.Codecs.FlexSearch
    open FlexLucene.Util
    
    /// Corresponds to Lucene Index version. There will
    /// always be a default codec associated with each index version.
    type T = 
        | Undefined = 0
        /// Lucene 4.9 index format
        | Lucene_4_9 = 1
        /// Lucene 4.10 index format
        | Lucene_4_10 = 2
        /// Lucene 4.10.1 index format
        | Lucene_4_10_1 = 3
        /// Lucene 5.0.0 index format
        | Lucene_5_0_0 = 4
    
    /// Build Lucene index version from FlexSearch index version    
    let build = 
        function 
        | T.Lucene_4_9 -> ok (Version.LUCENE_4_9)
        | T.Lucene_4_10 -> ok (Version.LUCENE_4_10_0)
        | T.Lucene_4_10_1 -> ok (Version.LUCENE_4_10_1)
        | T.Lucene_5_0_0 -> ok (Version.LUCENE_5_0_0)
        | unknown -> fail (UnSupportedIndexVersion(unknown.ToString()))
    
    /// Get the default codec associated with an index version
    let getDefaultCodec = 
        function 
        | T.Lucene_4_9 -> ok (new FlexCodec410() :> Codec)
        | T.Lucene_4_10 -> ok (new FlexCodec410() :> Codec)
        | T.Lucene_4_10_1 -> ok (new FlexCodec410() :> Codec)
        | T.Lucene_5_0_0 -> ok (new FlexCodec50() :> Codec)
        | unknown -> fail (UnSupportedIndexVersion(unknown.ToString()))
    
    /// Get the default codec associated with an index version
    let getDefaultPostingsFormat = 
        function 
        | T.Lucene_4_9 -> ok (FieldPostingsFormat.T.Lucene_4_1)
        | T.Lucene_4_10 -> ok (FieldPostingsFormat.T.Lucene_4_1)
        | T.Lucene_4_10_1 -> ok (FieldPostingsFormat.T.Lucene_4_1)
        | T.Lucene_5_0_0 -> ok (FieldPostingsFormat.T.Lucene_5_0)
        | unknown -> fail (UnSupportedIndexVersion(unknown.ToString()))
    
    /// Get the id postings format associated with an index version
    let getIdFieldPostingsFormat (useBloomFilter) (version) = 
        match (version, useBloomFilter) with
        | T.Lucene_4_9, true -> ok (FieldPostingsFormat.T.Bloom_4_1)
        | T.Lucene_4_9, false -> ok (FieldPostingsFormat.T.Lucene_4_1)
        | T.Lucene_4_10, true -> ok (FieldPostingsFormat.T.Bloom_4_1)
        | T.Lucene_4_10, false -> ok (FieldPostingsFormat.T.Lucene_4_1)
        | T.Lucene_4_10_1, true -> ok (FieldPostingsFormat.T.Bloom_4_1)
        | T.Lucene_4_10_1, false -> ok (FieldPostingsFormat.T.Lucene_4_1)
        | T.Lucene_5_0_0, true -> ok (FieldPostingsFormat.T.Bloom_5_0)
        | T.Lucene_5_0_0, false -> ok (FieldPostingsFormat.T.Lucene_5_0)
        | unknown -> fail (UnSupportedIndexVersion(unknown.ToString()))

/// Scripts can be used to automate various processing in FlexSearch. Script Type signifies
/// the type of operation that the current script can perform. These can vary from scripts
/// used for computing fields dynamically at index time or scripts which can be used to alter
/// FlexSearch's default scoring.
type ScriptType = 
    | Undefined = 0
    /// Can be used to dynamically select a search profile based upon the given input.
    /// Not available in the current version.
    | SearchProfileSelector = 1
    /// Can be used to modify the default scoring of the engine.
    /// Not available at the moment.
    | CustomScoring = 2
    /// Can be used to dynamically compute fields at index time. For example one can write
    /// a script to generate full name automatically from first name and last name. These
    /// get executed at index time only.
    | ComputedField = 3

///// Represents the current state of the index.
//type IndexState = 
//    | Undefined = 0
//    /// Index is opening. 
//    | Opening = 1
//    /// Index is Online.
//    | Online = 2
//    /// Index is off-line.
//    | Offline = 3
//    /// Index is closing
//    | Closing = 4
/// Represents the status of job.
type JobStatus = 
    | Undefined = 0
    /// Job is currently initializing. This essentially means that the job is added to the 
    /// job queue but has started executing. Depending upon the type of connector a job can take
    /// a while to move from this status. This also depends upon the parallel capability of the
    // connector. Most of the connectors are designed to perform one operation at a time. 
    | Initializing = 1
    /// Job is initialized. All the parameters supplied as a part of the job are correct and job has 
    /// been scheduled successfully.
    | Initialized = 2
    /// Job is currently being executed by the engine. 
    | InProgress = 3
    /// Job has finished without errors.
    | Completed = 4
    /// Job has finished with errors. Check the message property to get error details. Jobs have access to
    /// the engine's logging service so they could potentially write more error information to the logs.
    | CompletedWithErrors = 5

/// Missing Value option is used by FlexSearch in conjunction with Search profile based query. This tells the
/// engine about how to resolve an error if a field which is used in the search profile is not supplied by the 
/// caller. For example, if the profile requires `firstname` and it is not supplied by the caller then the
/// missing value option can be used to configure the engine's behaviour.
type MissingValueOption = 
    | Undefined = 0
    /// Throw an error with the information about the missing field.
    | ThrowError = 1
    /// Use the default value supplied by the user as a part of search profile configuration.
    | Default = 2
    /// Ignore the missing field related conditions. This is essentially replacing the condition with
    /// a match all condition.
    | Ignore = 3

///  Advance field properties to be used by custom field
type FieldIndexingInformation = 
    { Index : bool
      Tokenize : bool
      /// This maps to Lucene's term vectors and is only used for flex custom
      /// data type
      FieldTermVector : FieldTermVector
      /// This maps to Lucene's field index options
      FieldIndexOptions : FieldIndexOptions }

[<RequireQualifiedAccessAttribute>]
module FieldType = 
    open FlexSearch.Core
    
    /// The field type defines how FlexSearch should interpret data in a field and how the 
    /// field can be queried. There are many field types included with FlexSearch by default, 
    /// and custom types can also be defined.
    type Dto = 
        // Case to handle deserialization to default value
        | Undefined = 0
        /// Integer
        | Int = 1
        /// Double
        | Double = 2
        /// Field to store keywords. The entire input will be treated as a single word. This is 
        /// useful for fields like customerid, referenceid etc. These fields only support complete 
        /// text matching while searching and no partial word match is available.
        | ExactText = 3
        /// General purpose field to store normal textual data
        | Text = 4
        /// Similar to Text field but supports highlighting of search results
        | Highlight = 5
        /// Boolean
        | Bool = 6
        /// Fixed format date field (Supported format: YYYYmmdd)
        | Date = 7
        /// Fixed format datetime field (Supported format: YYYYMMDDhhmmss)
        | DateTime = 8
        /// Custom field type which gives more granular control over the field configuration
        | Custom = 9
        /// Non-indexed field. Only used for retrieving stored text. Searching is not
        /// possible over these fields.
        | Stored = 10
        /// Long
        | Long = 11
    
    /// Represents the various data types supported by Flex
    type T = 
        | Stored
        | Custom of searchAnalyzer : Analyzer * indexAnalyzer : Analyzer * indexingInformation : FieldIndexingInformation
        | Highlight of searchAnalyzer : Analyzer * indexAnalyzer : Analyzer
        | Text of searchAnalyzer : Analyzer * indexAnalyzer : Analyzer
        | ExactText of analyzer : Analyzer
        | Bool of analyzer : Analyzer
        | Date
        | DateTime
        | Int
        | Double
        | Long
    
    /// Check if the passed field is numeric field
    let inline isNumericField (f : T) = 
        match f with
        | Date | DateTime | Int | Double | Long -> true
        | _ -> false
    
    /// Checks if a given field type requires an analyzer
    let inline requiresAnalyzer f = 
        match f with
        | Custom(_, _, _) -> true
        | Text(_) -> true
        | Bool(_) -> true
        | ExactText(_) -> true
        | Highlight(_) -> true
        | Stored(_) -> false
        | Date(_) -> false
        | DateTime(_) -> false
        | Int(_) -> false
        | Double(_) -> false
        | Long(_) -> false
    
    /// Checks if a given field type requires an analyzer
    let inline searchable f = 
        match f with
        | Stored -> false
        | _ -> true
    
    /// Gets the default string value associated with the field type.
    let inline defaultValue f = 
        match f with
        | Custom(_, _, _) -> "null"
        | Stored(_) -> "null"
        | Text(_) -> "null"
        | Bool(_) -> "false"
        | ExactText(_) -> "null"
        | Date(_) -> "00010101"
        | DateTime(_) -> "00010101000000"
        | Int(_) -> "0"
        | Double(_) -> "0.0"
        | Highlight(_) -> "null"
        | Long(_) -> "0"
    
    /// Gets the sort field associated with the field type. This is used for determining sort style
    let inline sortField f = 
        match f with
        | Custom(_, _, _) -> failwithf "Sorting is not possible on string or text data type."
        | Stored(_) -> failwithf "Sorting is not possible on store only data type."
        | Text(_) -> failwithf "Sorting is not possible on string or text data type."
        | Bool(_) -> SortField.Type.STRING
        | ExactText(_) -> SortField.Type.STRING
        | Date(_) -> SortField.Type.LONG
        | DateTime(_) -> SortField.Type.LONG
        | Int(_) -> SortField.Type.INT
        | Double(_) -> SortField.Type.DOUBLE
        | Highlight(_) -> failwithf "Sorting is not possible on string or text data type."
        | Long(_) -> SortField.Type.LONG

// ----------------------------------------------------------------------------
// Search profile related types
// ----------------------------------------------------------------------------
type ScriptsManager = 
    { ComputedFieldScripts : Dictionary<string, System.Func<System.Dynamic.DynamicObject, string>>
      ProfileSelectorScripts : Dictionary<string, System.Func<System.Dynamic.DynamicObject, string>>
      CustomScoringScripts : Dictionary<string, System.Dynamic.DynamicObject * double -> double> }

module ShardConfiguration = 
    /// Allows to control various Index Shards related settings.
    [<CLIMutableAttribute>]
    type T = 
        { /// Total number of shards to be present in the given index.
          ShardCount : int }
        static member Default = { ShardCount = 1 }
        interface IValidate<T> with
            
            member this.SetDefaults() = 
                if this.ShardCount = 0 then T.Default
                else this
            
            member this.Validate() = this.ShardCount |> gt ("ShardCount") 1

[<RequireQualifiedAccessAttribute>]
module IndexConfiguration = 
    /// Allows to control various Index related settings.
    type T = 
        { /// The amount of time in seconds that FlexSearch 
          /// should wait before committing changes to the disk.
          CommitTimeSeconds : int
          /// A Directory is a flat list of files. Files may be 
          /// written once, when they are created. Once a file 
          /// is created it may only be opened for read, or 
          /// deleted. Random access is permitted both when 
          /// reading and writing.
          DirectoryType : DirectoryType.T
          /// The default maximum time to wait for a write 
          /// lock (in milliseconds).
          DefaultWriteLockTimeout : int
          /// Determines the amount of RAM that may be used 
          /// for buffering added documents and deletions 
          /// before they are flushed to the Directory.
          RamBufferSizeMb : int
          /// The number of buffered added documents that will 
          /// trigger a flush if enabled.
          MaxBufferedDocs : int
          /// The amount of time in milliseconds that FlexSearch 
          /// should wait before reopening index reader. This 
          /// helps in keeping writing and real time aspects of 
          /// the engine separate.
          RefreshTimeMilliseconds : int
          /// Corresponds to Lucene Index version. There will
          /// always be a default codec associated with each 
          /// index version.
          IndexVersion : IndexVersion.T
          /// Signifies if bloom filter should be used for 
          /// encoding Id field.
          UseBloomFilterForId : Nullable<bool>
          /// This will be computed at run time based on the 
          /// index version
          IdIndexPostingsFormat : FieldPostingsFormat.T
          /// This will be computed at run time based on the index version
          DefaultIndexPostingsFormat : FieldPostingsFormat.T
          /// Similarity defines the components of Lucene scoring. Similarity
          /// determines how Lucene weights terms and Lucene interacts with 
          /// Similarity at both index-time and query-time.
          DefaultFieldSimilarity : FieldSimilarity.T }
        
        static member Default = 
            { CommitTimeSeconds = 300
              DirectoryType = DirectoryType.T.MemoryMapped
              DefaultWriteLockTimeout = 1000
              RamBufferSizeMb = 100
              MaxBufferedDocs = -1
              RefreshTimeMilliseconds = 500
              IndexVersion = IndexVersion.T.Lucene_5_0_0
              UseBloomFilterForId = Nullable(true)
              IdIndexPostingsFormat = Unchecked.defaultof<FieldPostingsFormat.T>
              DefaultIndexPostingsFormat = Unchecked.defaultof<FieldPostingsFormat.T>
              DefaultFieldSimilarity = FieldSimilarity.T.TFIDF }
        
        interface IValidate<T> with
            
            member this.SetDefaults() = 
                { CommitTimeSeconds = this.CommitTimeSeconds |> Args.int T.Default.CommitTimeSeconds
                  DirectoryType = this.DirectoryType |> Args.enum T.Default.DirectoryType
                  DefaultWriteLockTimeout = this.DefaultWriteLockTimeout |> Args.int T.Default.DefaultWriteLockTimeout
                  RamBufferSizeMb = this.RamBufferSizeMb |> Args.int T.Default.RamBufferSizeMb
                  MaxBufferedDocs = this.MaxBufferedDocs |> Args.int T.Default.MaxBufferedDocs
                  RefreshTimeMilliseconds = this.RefreshTimeMilliseconds |> Args.int T.Default.RefreshTimeMilliseconds
                  IndexVersion = this.IndexVersion |> Args.enum T.Default.IndexVersion
                  DefaultFieldSimilarity = this.DefaultFieldSimilarity |> Args.enum T.Default.DefaultFieldSimilarity
                  UseBloomFilterForId = this.UseBloomFilterForId |> Args.bool T.Default.UseBloomFilterForId
                  IdIndexPostingsFormat = this.IdIndexPostingsFormat
                  DefaultIndexPostingsFormat = this.DefaultIndexPostingsFormat }
            
            member this.Validate() = this.CommitTimeSeconds
                                     |> gte "CommitTimeSeconds" 30
                                     >>= (fun _ -> this.MaxBufferedDocs |> gte "MaxBufferedDocs" 2)
                                     >>= (fun _ -> this.RamBufferSizeMb |> gte "RamBufferSizeMb" 20)
                                     >>= (fun _ -> this.RefreshTimeMilliseconds |> gte "RefreshTimeMilliseconds" 25)
    
    let inline getIndexWriterConfiguration (codec : Codec) (similarity : Similarity) (indexAnalyzer : Analyzer) 
               (configuration : T) = 
        let iwc = new IndexWriterConfig(indexAnalyzer)
        iwc.SetOpenMode(IndexWriterConfig.OpenMode.CREATE_OR_APPEND) |> ignore
        iwc.SetRAMBufferSizeMB(float configuration.RamBufferSizeMb) |> ignore
        iwc.SetCodec(codec).SetSimilarity(similarity) |> ignore
        iwc

module Analyzer = 
    /// Filters consume input and produce a stream of tokens. In most cases a filter looks 
    /// at each token in the stream sequentially and decides whether to pass it along, 
    /// replace it or discard it. A filter may also do more complex analysis by looking 
    /// ahead to consider multiple tokens at once, although this is less common. 
    type TokenFilter = 
        { /// The name of the filter. Some pre-defined filters are the following-
          /// + Ascii Folding Filter
          /// + Standard Filter
          /// + Beider Morse Filter
          /// + Double Metaphone Filter
          /// + Caverphone2 Filter
          /// + Metaphone Filter
          /// + Refined Soundex Filter
          /// + Soundex Filter
          /// For more details refer to http://flexsearch.net/docs/concepts/understanding-analyzers-tokenizers-and-filters/
          FilterName : string
          /// Parameters required by the filter.
          mutable Parameters : Dictionary<string, string> }
        
        static member Default = 
            { FilterName = Unchecked.defaultof<_>
              Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) }
        
        interface IValidate<TokenFilter> with
            
            member this.SetDefaults() = 
                if isNull (this.Parameters) then this.Parameters <- TokenFilter.Default.Parameters
                this
            
            member this.Validate() = this.FilterName |> propertyNameValidator "FilterName"
    
    /// Tokenizer breaks up a stream of text into tokens, where each token is a sub-sequence
    /// of the characters in the text. An analyzer is aware of the field it is configured 
    /// for, but a tokenizer is not.
    type Tokenizer = 
        { /// The name of the tokenizer. Some pre-defined tokenizers are the following-
          /// + Standard Tokenizer
          /// + Classic Tokenizer
          /// + Keyword Tokenizer
          /// + Letter Tokenizer
          /// + Lower Case Tokenizer
          /// + UAX29 URL Email Tokenizer
          /// + White Space Tokenizer
          /// For more details refer to http://flexsearch.net/docs/concepts/understanding-analyzers-tokenizers-and-filters/
          TokenizerName : string
          /// Parameters required by the tokenizer.
          mutable Parameters : Dictionary<string, string> }
        
        static member Default = 
            { TokenizerName = Unchecked.defaultof<_>
              Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) }
        
        interface IValidate<Tokenizer> with
            
            member this.SetDefaults() = 
                if isNull (this.Parameters) then this.Parameters <- Tokenizer.Default.Parameters
                this
            
            member this.Validate() = this.TokenizerName |> propertyNameValidator "TokenizerName"
    
    /// An analyzer examines the text of fields and generates a token stream.
    type T = 
        { /// Name of the analyzer
          AnalyzerName : string
          // AUTO
          Tokenizer : Tokenizer
          /// Filters to be used by the analyzer.
          Filters : List<TokenFilter> }
        interface IValidate<T> with
            
            member this.SetDefaults() = 
                if isNull this.Filters then { this with Filters = new List<TokenFilter>() }
                else this
            
            member this.Validate() = this.AnalyzerName
                                     |> propertyNameValidator "AnalyzerName"
                                     >>= (this.Tokenizer :> IValidate<Tokenizer>).Validate
                                     >>= fun _ -> seqValidator (this.Filters.Cast<IValidate>())
    
    /// Build a Lucene Analyzer from FlexSearch Analyzer DTO
    let build (def : T) = 
        let builder = CustomAnalyzer.Builder()
        builder.withTokenizer (def.Tokenizer.TokenizerName, dictToMap (def.Tokenizer.Parameters)) |> ignore
        def.Filters |> Seq.iter (fun f -> builder.addTokenFilter (f.FilterName, dictToMap (f.Parameters)) |> ignore)
        try 
            ok (builder.build() :> Analyzer)
        with ex -> fail (AnalyzerBuilder(def.AnalyzerName, ex.Message, exceptionPrinter (ex)))

[<RequireQualifiedAccessAttribute>]
module Script = 
    open Microsoft.CSharp
    open System.CodeDom.Compiler
    
    /// Script is used to add scripting capability to the index. These can be used to generate dynamic
    /// field values based upon other indexed values or to modify scores of the returned results.
    /// Any valid C# expression can be used as a script.
    type T = 
        { /// Name of the script.
          ScriptName : string
          /// Source code of the script. 
          Source : string
          /// AUTO
          ScriptType : ScriptType }
        interface IValidate<T> with
            
            member this.SetDefaults() = 
                if (int) this.ScriptType = 0 then { this with ScriptType = ScriptType.ComputedField }
                else this
            
            member this.Validate() = this.ScriptName
                                     |> propertyNameValidator "ScriptName"
                                     >>= fun _ -> this.Source |> notBlank "Source"
    
    /// Template method code for computed field script
    let private computedFieldScriptTemplate = """
class Foo {
    static public string Execute(dynamic fields) { [SourceCode] }
}
"""
    
    /// Compiles the given string to a function
    let compileScript (sourceCode : string) = 
        try 
            let ccp = new CSharpCodeProvider()
            let cp = new CompilerParameters()
            cp.ReferencedAssemblies.Add("Microsoft.CSharp.dll") |> ignore
            cp.ReferencedAssemblies.Add("System.dll") |> ignore
            cp.ReferencedAssemblies.Add("System.Core.dll") |> ignore
            cp.GenerateExecutable <- false
            cp.IncludeDebugInformation <- false
            cp.GenerateInMemory <- true
            let cr = ccp.CompileAssemblyFromSource(cp, sourceCode)
            let foo = cr.CompiledAssembly.GetType("Foo")
            let meth = foo.GetMethod("Execute")
            let compiledScript = 
                Delegate.CreateDelegate(typeof<System.Func<System.Dynamic.DynamicObject, string>>, meth) :?> System.Func<System.Dynamic.DynamicObject, string>
            ok (compiledScript)
        with e -> fail (ScriptCannotBeCompiled(exceptionPrinter e))
    
    /// Compile computed field script
    let compileComputedFieldScript (src) = compileScript (computedFieldScriptTemplate.Replace("[SourceCode]", src))

[<RequireQualifiedAccessAttribute>]
module Field = 
    /// A field is a section of a Document. 
    /// <para>
    /// Fields can contain different kinds of data. A name field, for example, 
    /// is text (character data). A shoe size field might be a floating point number 
    /// so that it could contain values like 6 and 9.5. Obviously, the definition of 
    /// fields is flexible (you could define a shoe size field as a text field rather
    /// than a floating point number, for example), but if you define your fields correctly, 
    /// FlexSearch will be able to interpret them correctly and your users will get better 
    /// results when they perform a query.
    /// </para>
    /// <para>
    /// You can tell FlexSearch about the kind of data a field contains by specifying its 
    /// field type. The field type tells FlexSearch how to interpret the field and how 
    /// it can be queried. When you add a document, FlexSearch takes the information in 
    /// the document’s fields and adds that information to an index. When you perform a 
    /// query, FlexSearch can quickly consult the index and return the matching documents.
    /// </para>
    type Dto = 
        { /// Name of the field.
          FieldName : string
          /// Signifies if the field should be analyzed using an analyzer. 
          /// TODO: See if this is still needed post Lucene 5.0?
          Analyze : Nullable<bool>
          /// Signifies if a field should be indexed. A field can only be 
          /// stored without indexing.
          Index : Nullable<bool>
          /// Signifies if a field should be stored so that it can retrieved
          /// while searching.
          Store : Nullable<bool>
          /// Analyzer to be used while indexing.
          IndexAnalyzer : string
          /// Analyzer to be used while searching.
          SearchAnalyzer : string
          /// AUTO
          FieldType : FieldType.Dto
          /// AUTO
          Similarity : FieldSimilarity.T
          /// AUTO
          IndexOptions : FieldIndexOptions
          /// AUTO
          TermVector : FieldTermVector
          /// If true, omits the norms associated with this field (this disables length 
          /// normalization and index-time boosting for the field, and saves some memory). 
          /// Defaults to true for all primitive (non-analyzed) field types, such as int, 
          /// float, data, bool, and string. Only full-text fields or fields that need an 
          /// index-time boost need norms.
          OmitNorms : Nullable<bool>
          /// Fields can get their content dynamically through scripts. This is the name of 
          /// the script to be used for getting field data at index time.
          ScriptName : string }
        
        static member Default = 
            { FieldName = Unchecked.defaultof<string>
              Analyze = Nullable(true)
              Index = Nullable(true)
              Store = Nullable(true)
              IndexAnalyzer = StandardAnalyzer
              SearchAnalyzer = StandardAnalyzer
              FieldType = FieldType.Dto.Text
              Similarity = FieldSimilarity.T.TFIDF
              IndexOptions = FieldIndexOptions.DocsAndFreqsAndPositions
              TermVector = FieldTermVector.DoNotStoreTermVector
              OmitNorms = Nullable(true)
              ScriptName = Unchecked.defaultof<string> }
        
        interface IValidate<Dto> with
            
            member this.SetDefaults() = 
                { FieldName = this.FieldName
                  Analyze = this.Analyze |> Args.bool Dto.Default.Analyze
                  Index = this.Index |> Args.bool Dto.Default.Index
                  Store = this.Store |> Args.bool Dto.Default.Store
                  IndexAnalyzer = this.IndexAnalyzer |> Args.string Dto.Default.IndexAnalyzer
                  SearchAnalyzer = this.IndexAnalyzer |> Args.string Dto.Default.IndexAnalyzer
                  FieldType = this.FieldType |> Args.enum Dto.Default.FieldType
                  Similarity = this.Similarity |> Args.enum Dto.Default.Similarity
                  IndexOptions = this.IndexOptions |> Args.enum Dto.Default.IndexOptions
                  TermVector = this.TermVector |> Args.enum Dto.Default.TermVector
                  OmitNorms = this.OmitNorms |> Args.bool Dto.Default.OmitNorms
                  ScriptName = this.ScriptName }
            
            member this.Validate() = 
                this.FieldName
                |> propertyNameValidator "FieldName"
                >>= (fun _ -> 
                if (this.FieldType = FieldType.Dto.Text || this.FieldType = FieldType.Dto.Highlight 
                    || this.FieldType = FieldType.Dto.Custom) 
                   && (String.IsNullOrWhiteSpace(this.SearchAnalyzer) || String.IsNullOrWhiteSpace(this.IndexAnalyzer)) then 
                    fail (AnalyzerIsMandatory(this.FieldName))
                else ok())
    
    /// General Field which represents the basic properties for the field to be indexed
    type T = 
        { FieldName : string
          SchemaName : string
          IsStored : bool
          Similarity : FieldSimilarity.T
          FieldType : FieldType.T
          Source : System.Func<System.Dynamic.DynamicObject, string> option
          /// Computed Information - Mostly helpers to avoid matching over Field type
          /// Helper property to determine if the field needs any analyzer.
          RequiresAnalyzer : bool
          /// Signifies if the field is searchable. Stored Field types are not
          /// searchable.
          Searchable : bool }
    
    /// Field info to be used by flex highlight field
    let flexHighLightFieldType = 
        lazy (let fieldType = new FieldType()
              fieldType.SetStored(true)
              fieldType.SetTokenized(true)
              fieldType.SetIndexOptions(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS_AND_OFFSETS)
              fieldType.Freeze()
              fieldType)
    
    /// Creates Lucene's field types. This is only used for FlexCustom data type to
    /// support flexible field type
    let getFieldTemplate (fieldTermVector : FieldTermVector, stored, tokenized, indexed) = 
        let fieldType = new FieldType()
        fieldType.SetStored(stored)
        fieldType.SetTokenized(tokenized)
        match fieldTermVector with
        | FieldTermVector.DoNotStoreTermVector -> fieldType.SetIndexOptions(IndexOptions.DOCS)
        | FieldTermVector.StoreTermVector -> fieldType.SetIndexOptions(IndexOptions.DOCS_AND_FREQS)
        | FieldTermVector.StoreTermVectorsWithPositions -> 
            fieldType.SetIndexOptions(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS)
        | FieldTermVector.StoreTermVectorsWithPositionsandOffsets -> 
            fieldType.SetIndexOptions(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS_AND_OFFSETS)
        | _ -> failwithf "Invalid Field term vector"
        fieldType
    
    let store = Field.Store.YES
    let doNotStore = Field.Store.NO
    let getStringField (fieldName, value, store) = new StringField(fieldName, value, store) :> Field
    let getTextField (fieldName, value, store) = new TextField(fieldName, value, store) :> Field
    let getLongField (fieldName, value : int64, store : Field.Store) = new LongField(fieldName, value, store) :> Field
    let getIntField (fieldName, value : int32, store : Field.Store) = new IntField(fieldName, value, store) :> Field
    let getDoubleField (fieldName, value : float, store : Field.Store) = 
        new DoubleField(fieldName, value, store) :> Field
    let getStoredField (fieldName, value : string) = new StoredField(fieldName, value) :> Field
    let getField (fieldName, value : string, template : FlexLucene.Document.FieldType) = 
        new Field(fieldName, value, template)
    
    /// Set the value of index field using the passed value
    let inline updateLuceneField flexField (lucenceField : Field) (value : string) = 
        match flexField.FieldType with
        | FieldType.Custom(_, _, _) -> lucenceField.SetStringValue(value)
        | FieldType.Stored -> lucenceField.SetStringValue(value)
        | FieldType.Text(_) -> lucenceField.SetStringValue(value)
        | FieldType.Highlight(_) -> lucenceField.SetStringValue(value)
        | FieldType.ExactText(_) -> lucenceField.SetStringValue(value)
        | FieldType.Bool(_) -> (value |> pBool false).ToString() |> lucenceField.SetStringValue
        | FieldType.Date -> (value |> pLong DateDefaultValue) |> lucenceField.SetLongValue
        | FieldType.DateTime -> (value |> pLong DateTimeDefaultValue) |> lucenceField.SetLongValue
        | FieldType.Int -> (value |> pInt 0) |> lucenceField.SetIntValue
        | FieldType.Double -> (value |> pDouble 0.0) |> lucenceField.SetDoubleValue
        | FieldType.Long -> (value |> pLong (int64 0)) |> lucenceField.SetLongValue
    
    let inline storeInfoMap (isStored) = 
        if isStored then Field.Store.YES
        else Field.Store.NO
    
    /// Creates a default Lucene index field for the passed flex field.
    let inline createDefaultLuceneField flexField = 
        let storeInfo = storeInfoMap (flexField.IsStored)
        match flexField.FieldType with
        | FieldType.Custom(_, _, b) -> 
            getField 
                (flexField.SchemaName, "null", 
                 getFieldTemplate (b.FieldTermVector, flexField.IsStored, b.Tokenize, b.Index))
        | FieldType.Stored -> getStoredField (flexField.SchemaName, "null")
        | FieldType.Text(_) -> getTextField (flexField.SchemaName, "null", storeInfoMap (flexField.IsStored))
        | FieldType.Highlight(_) -> getField (flexField.SchemaName, "null", flexHighLightFieldType.Value)
        | FieldType.ExactText(_) -> getTextField (flexField.SchemaName, "null", storeInfo)
        | FieldType.Bool(_) -> getTextField (flexField.SchemaName, "false", storeInfo)
        | FieldType.Date -> getLongField (flexField.SchemaName, DateDefaultValue, storeInfo)
        | FieldType.DateTime -> getLongField (flexField.SchemaName, DateTimeDefaultValue, storeInfo)
        | FieldType.Int -> getIntField (flexField.SchemaName, 0, storeInfo)
        | FieldType.Double -> getDoubleField (flexField.SchemaName, 0.0, storeInfo)
        | FieldType.Long -> getLongField (flexField.SchemaName, int64 0, storeInfo)
    
    /// Get a search query parser associated with the field 
    let inline getSearchAnalyzer (flexField : T) = 
        match flexField.FieldType with
        | FieldType.Custom(a, b, c) -> Some(a)
        | FieldType.Highlight(a, _) -> Some(a)
        | FieldType.Text(a, _) -> Some(a)
        | FieldType.ExactText(a) -> Some(a)
        | FieldType.Bool(a) -> Some(a)
        | FieldType.Date | FieldType.DateTime | FieldType.Int | FieldType.Double | FieldType.Stored | FieldType.Long -> 
            None
    
    /// Set the value of index field to the default value
    let inline updateLuceneFieldToDefault flexField (luceneField : Field) = 
        match flexField.FieldType with
        | FieldType.Custom(_, _, _) -> luceneField.SetStringValue("null")
        | FieldType.Stored -> luceneField.SetStringValue("null")
        | FieldType.Text(_) -> luceneField.SetStringValue("null")
        | FieldType.Bool(_) -> luceneField.SetStringValue("false")
        | FieldType.ExactText(_) -> luceneField.SetStringValue("null")
        | FieldType.Highlight(_) -> luceneField.SetStringValue("null")
        | FieldType.Date -> luceneField.SetLongValue(DateDefaultValue)
        | FieldType.DateTime -> luceneField.SetLongValue(DateTimeDefaultValue)
        | FieldType.Int -> luceneField.SetIntValue(0)
        | FieldType.Double -> luceneField.SetDoubleValue(0.0)
        | FieldType.Long -> luceneField.SetLongValue(int64 0)
    
    /// Get the schema name for a field from the name and postings format
    let schemaName (fieldName, postingsFormat : string) = sprintf "%s<%s>" fieldName (postingsFormat.ToLowerInvariant())
    
    let create (fieldName : string, postingsFormat : FieldPostingsFormat.T, fieldType : FieldType.T) = 
        { FieldName = fieldName
          SchemaName = schemaName (fieldName, postingsFormat.ToString())
          IsStored = true
          FieldType = fieldType
          Source = None
          Searchable = FieldType.searchable (fieldType)
          Similarity = FieldSimilarity.T.TFIDF
          RequiresAnalyzer = FieldType.requiresAnalyzer (fieldType) }
    
    /// Field to be used by the Id field
    let getIdField (postingsFormat : FieldPostingsFormat.T) = 
        let indexInformation = 
            { Index = true
              Tokenize = false
              FieldTermVector = FieldTermVector.DoNotStoreTermVector
              FieldIndexOptions = FieldIndexOptions.DocsOnly }
        create 
            (Constants.IdField, postingsFormat, 
             FieldType.Custom(CaseInsensitiveKeywordAnalyzer, CaseInsensitiveKeywordAnalyzer, indexInformation))
    
    /// Field to be used by time stamp
    let getTimeStampField (postingsFormat : FieldPostingsFormat.T) = 
        create (Constants.LastModifiedField, postingsFormat, FieldType.DateTime)
    
    /// Build FlexField from field
    let build (field : Dto, indexConfiguration : IndexConfiguration.T, 
               analyzerFactory : LazyFactory.T<Analyzer, Analyzer.T, _>, scriptsManager : ScriptsManager) = 
        let getSource (field : Dto) = 
            if (String.IsNullOrWhiteSpace(field.ScriptName)) then ok (None)
            else 
                match scriptsManager.ComputedFieldScripts.TryGetValue(field.ScriptName) with
                | true, a -> ok (Some(a))
                | _ -> fail (ScriptNotFound(field.ScriptName, field.FieldName))
        
        let getFieldType (field : Dto) = 
            maybe { 
                match field.FieldType with
                | FieldType.Dto.Int -> return FieldType.Int
                | FieldType.Dto.Double -> return FieldType.Double
                | FieldType.Dto.Bool -> return FieldType.Bool(CaseInsensitiveKeywordAnalyzer)
                | FieldType.Dto.Date -> return FieldType.Date
                | FieldType.Dto.DateTime -> return FieldType.DateTime
                | FieldType.Dto.Long -> return FieldType.Long
                | FieldType.Dto.Stored -> return FieldType.Stored
                | FieldType.Dto.ExactText -> return FieldType.ExactText(CaseInsensitiveKeywordAnalyzer)
                | FieldType.Dto.Text | FieldType.Dto.Highlight | FieldType.Dto.Custom -> 
                    let! searchAnalyzer = analyzerFactory |> LazyFactory.getInstance field.SearchAnalyzer
                    let! indexAnalyzer = analyzerFactory |> LazyFactory.getInstance field.IndexAnalyzer
                    match field.FieldType with
                    | FieldType.Dto.Text -> return FieldType.Text(searchAnalyzer, indexAnalyzer)
                    | FieldType.Dto.Highlight -> return FieldType.Highlight(searchAnalyzer, indexAnalyzer)
                    | FieldType.Dto.Custom -> 
                        let indexingInformation = 
                            { Index = field.Index |> Args.getBool
                              Tokenize = field.Analyze |> Args.getBool
                              FieldTermVector = field.TermVector
                              FieldIndexOptions = field.IndexOptions }
                        return FieldType.Custom(searchAnalyzer, indexAnalyzer, indexingInformation)
                    | _ -> return! fail (AnalyzerNotSupportedForFieldType(field.FieldName, field.FieldType.ToString()))
                | _ -> return! fail (UnSupportedFieldType(field.FieldName, field.FieldType.ToString()))
            }
        
        maybe { 
            let! source = getSource (field)
            let! fieldType = getFieldType (field)
            return { FieldName = field.FieldName
                     SchemaName = schemaName (field.FieldName, indexConfiguration.DefaultIndexPostingsFormat.ToString())
                     FieldType = fieldType
                     Source = source
                     Similarity = field.Similarity
                     IsStored = Args.getBool (field.Store)
                     Searchable = FieldType.searchable (fieldType)
                     RequiresAnalyzer = FieldType.requiresAnalyzer (fieldType) }
        }

module SearchQuery = 
    /// Used for configuring the settings for text highlighting in the search results
    type HighlightOption = 
        { /// Total number of fragments to return per document
          mutable FragmentsToReturn : int
          /// The fields to be used for text highlighting
          HighlightedFields : string array
          /// Post tag to represent the ending of the highlighted word
          mutable PostTag : string
          /// Pre tag to represent the ending of the highlighted word
          mutable PreTag : string }
        
        static member Default = 
            { FragmentsToReturn = 2
              HighlightedFields = Unchecked.defaultof<_>
              PostTag = "</B>"
              PreTag = "<B>" }
        
        interface IValidate<HighlightOption> with
            
            member this.SetDefaults() = 
                this.FragmentsToReturn <- this.FragmentsToReturn |> Args.int HighlightOption.Default.FragmentsToReturn
                this.PostTag <- this.PostTag |> Args.string "</B>"
                this.PreTag <- this.PreTag |> Args.string "<B>"
                this
            
            member __.Validate() = ok()
    
    /// Search query is used for searching over a FlexSearch index. This provides
    /// a consistent syntax to execute various types of queries. The syntax is similar
    /// to the SQL syntax. This was done on purpose to reduce the learning curve.
    type T = 
        { /// Unique name of the query. This is only required if you are setting up a 
          /// search profile.
          QueryName : string
          /// Columns to be returned as part of results.
          /// + *  - return all columns
          /// + [] - return no columns
          /// + ["columnName"] -  return specific column
          mutable Columns : string array
          /// Count of results to be returned
          mutable Count : int
          /// AUTO
          Highlights : HighlightOption
          /// Name of the index
          IndexName : string
          /// Can be used to order the results by score or specific field.
          mutable OrderBy : string
          /// Used to enable paging and skip certain pre-fetched results.
          Skip : int //= 0 with get, set
          /// Query string to be used for searching
          QueryString : string
          /// AUTO
          MissingValueConfiguration : Dictionary<string, MissingValueOption>
          /// Universal configuration for the missing field values. Only applicable
          /// for search profiles.
          GlobalMissingValue : MissingValueOption
          /// If true will return collapsed search results which are in tabular form.
          /// Flat results enable easy binding to a grid but grouping results is tougher
          /// with Flat result.
          ReturnFlatResult : bool
          /// If true then scores are returned as a part of search result.
          ReturnScore : bool
          /// Profile Name to be used for profile based searching.
          SearchProfile : string
          /// Script which can be used to select a search profile. This can help in
          /// dynamic selection of search profile based on the incoming data.
          SearchProfileSelector : string }
        
        static member Default = 
            { QueryName = Unchecked.defaultof<_>
              Columns = [||]
              Count = 10
              Highlights = Unchecked.defaultof<_>
              IndexName = Unchecked.defaultof<_>
              OrderBy = "score"
              Skip = 0
              QueryString = Unchecked.defaultof<_>
              MissingValueConfiguration = Unchecked.defaultof<_>
              GlobalMissingValue = MissingValueOption.Default
              ReturnFlatResult = false
              ReturnScore = false
              SearchProfile = Unchecked.defaultof<string>
              SearchProfileSelector = Unchecked.defaultof<string> }
        
        interface IValidate<T> with
            
            member this.SetDefaults() = 
                if isNull this.Columns then this.Columns <- T.Default.Columns
                this.Count <- this.Count |> Args.int T.Default.Count
                this.OrderBy <- this.OrderBy |> Args.string T.Default.OrderBy
                this
            
            member this.Validate() = this.IndexName |> propertyNameValidator "IndexName"

[<RequireQualifiedAccessAttribute>]
module Document = 
    /// Represents the meta data associated with a document. Some of these might
    /// only be relevant for returned documents.
    type Meta = 
        { /// Any matched text highlighted snippets. Note: Only used for results
          Highlights : List<string>
          /// Score of the returned document. Note: Only used for results
          Score : double }
    
    /// A document represents the basic unit of information which can be added or retrieved from the index. 
    /// A document consists of several fields. A field represents the actual data to be indexed. In database 
    /// analogy an index can be considered as a table while a document is a row of that table. Like a table a 
    /// FlexSearch document requires a fix schema and all fields should have a field type.
    [<CLIMutableAttribute>]
    type T = 
        { /// Fields to be added to the document for indexing.
          Fields : Dictionary<string, string>
          /// Unique Id of the document
          Id : string
          /// Timestamp of the last modification of the document. This field is interpreted differently
          /// during a create and update operation. It also dictates whether and unique Id check is to be performed
          ///  or not. 
          /// Version number semantics
          /// + 0 - Don't care about the version and proceed with the operation normally.
          /// + -1 - Ensure that the document does not exist (Performs unique Id check).
          /// + 1 - Ensure that the document does exist. This is not relevant for create operation.
          /// > 1 - Ensure that the version matches exactly. This is not relevant for create operation.
          mutable TimeStamp : Int64
          /// mutable ModifyIndex : Int64
          /// Name of the index
          IndexName : string
          /// Any matched text highlighted snippets. Note: Only used for results
          mutable Highlights : List<string>
          /// Score of the returned document. Note: Only used for results
          mutable Score : double }
        
        static member Default = 
            { Fields = Unchecked.defaultof<_>
              Id = Unchecked.defaultof<_>
              TimeStamp = Unchecked.defaultof<_>
              IndexName = Unchecked.defaultof<_>
              Highlights = Unchecked.defaultof<_>
              Score = Unchecked.defaultof<_> }
        
        interface IValidate<T> with
            member this.SetDefaults() = this
            member this.Validate() = this.IndexName
                                     |> notBlank "IndexName"
                                     >>= fun _ -> this.Id |> notBlank "Id"

/// FlexSearch index is a logical index built on top of Lucene’s index in a manner 
/// to support features like schema and sharding. So in this sense a FlexSearch 
/// index consists of multiple Lucene’s index. Also, each FlexSearch shard is a valid 
/// Lucene index.
///
/// In case of a database analogy an index represents a table in a database where 
/// one has to define a schema upfront before performing any kind of operation on 
/// the table. There are various properties that can be defined at the index creation 
/// time. Only IndexName is a mandatory property, though one should always define 
/// Fields in an index to make any use of it.
///
/// By default a newly created index stays off-line. This is by design to force the 
/// user to enable an index before using it.
module Index = 
    type T = 
        { /// Name of the index
          IndexName : string
          /// Fields to be used in index.
          Fields : Field.Dto array
          /// Scripts to be used in index.
          Scripts : Script.T array
          /// Search Profiles
          SearchProfiles : SearchQuery.T array
          /// AUTO
          ShardConfiguration : ShardConfiguration.T
          /// AUTO
          IndexConfiguration : IndexConfiguration.T
          /// Signifies if the index is on-line or not? An index has to be 
          /// on-line in order to enable searching over it.
          Online : Nullable<bool> }
        
        static member Default = 
            { IndexName = Unchecked.defaultof<_>
              Fields = Unchecked.defaultof<_>
              Scripts = Unchecked.defaultof<_>
              SearchProfiles = Unchecked.defaultof<_>
              ShardConfiguration = ShardConfiguration.T.Default
              IndexConfiguration = IndexConfiguration.T.Default
              Online = Nullable(true) }
        
        interface IValidate<T> with
            
            member this.SetDefaults() = 
                { IndexName = this.IndexName
                  Fields = this.Fields |> Args.isNull Array.empty
                  Scripts = this.Scripts |> Args.isNull Array.empty
                  SearchProfiles = this.SearchProfiles |> Args.isNull Array.empty
                  ShardConfiguration = this.ShardConfiguration |> Args.isNull T.Default.ShardConfiguration
                  IndexConfiguration = this.IndexConfiguration |> Args.isNull T.Default.IndexConfiguration
                  Online = this.Online |> Args.bool T.Default.Online }
            
            member this.Validate() = 
                let checkDuplicateFieldName() = 
                    this.Fields.Select(fun x -> x.FieldName).ToArray() |> hasDuplicates "Fields" "FieldName"
                let checkDuplicateScriptNames() = 
                    this.Scripts.Select(fun x -> x.ScriptName).ToArray() |> hasDuplicates "Scripts" "ScriptName"
                let checkDuplicateQueries() = 
                    this.SearchProfiles.Select(fun x -> x.QueryName).ToArray() 
                    |> hasDuplicates "SearchProfiles" "QueryName"
                
                // Check if the script specified against a fields exists
                let checkScriptExists() = 
                    let result = 
                        this.Fields
                        |> Seq.map (fun field -> 
                               if String.IsNullOrWhiteSpace(field.ScriptName) = false then 
                                   if this.Scripts.FirstOrDefault(fun x -> x.ScriptName = field.ScriptName) = Unchecked.defaultof<Script.T> then 
                                       fail (ScriptNotFound(field.ScriptName, field.FieldName))
                                   else ok()
                               else ok())
                        |> Seq.filter (fun x -> failed x)
                        |> Seq.toArray
                    if result.Count() = 0 then ok()
                    else result.[0]
                
                let validateSearchQuery() = 
                    // Check if any query name is missing in search profiles. Cannot do this through annotation as the
                    // Query Name is not mandatory for normal Search Queries
                    let missingQueryNames = 
                        this.SearchProfiles |> Seq.filter (fun x -> String.IsNullOrWhiteSpace(x.QueryName))
                    if missingQueryNames.Count() <> 0 then fail (NotBlank("QueryName"))
                    else ok()
                
                this.IndexName
                |> propertyNameValidator "IndexName"
                >>= fun _ -> seqValidator (this.Fields.Cast<IValidate>())
                >>= fun _ -> seqValidator (this.Scripts.Cast<IValidate>())
                >>= fun _ -> seqValidator (this.SearchProfiles.Cast<IValidate>())
                >>= checkDuplicateFieldName
                >>= checkDuplicateScriptNames
                >>= validateSearchQuery
                >>= checkDuplicateQueries
                >>= checkScriptExists

//////////////////////////////////////////////////////////////////////////
/// Helper DTOs
//////////////////////////////////////////////////////////////////////////
/// Represents the result returned by FlexSearch for a given search query.
[<Sealed>]
type SearchResults() = 
    
    /// Documents which are returned as a part of search response.
    member val Documents = new List<Document.T>() with get, set
    
    /// Total number of records returned.
    member val RecordsReturned = 0 with get, set
    
    /// Total number of records available on the server. This could be 
    /// greater than the returned results depending upon the requested 
    /// document count. 
    member val TotalAvailable = 0 with get, set

///// Represents a list of words which can be used for filtering by an analyzer.
///// These can contain stop words or keep words etc.
//[<ToString; Sealed>]
//type FilterList(words : List<string>) = 
//    inherit ValidatableBase()
//    
//    /// List of words
//    member val Words = words with get, set
//    
//    new() = FilterList(Unchecked.defaultof<List<string>>)
//    override __.Validate() = ok()
//
///// Represents a list of words which can be used for synonym matching by an analyzer.
//[<ToString; Sealed>]
//type MapList(words : Dictionary<string, List<string>>) = 
//    inherit ValidatableBase()
//    
//    /// Words to be used for synonym matching.
//    member val Words = words with get, set
//    
//    new() = MapList(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase))
//    override __.Validate() = ok()
//
/// Used by long running processes. All long running FlexSearch operations create
/// an instance of Job and return the Id to the caller. This Id can be used by the
/// caller to check the status of the job.
///
/// NOTE: Job information is not persistent
//[<ToString; Sealed>]
//type Job() = 
//    inherit ValidatableBase()
//    
//    /// Unique Id of the Job
//    member val JobId = "" with get, set
//    
//    /// Total items to be processed as a part of the current job.
//    member val TotalItems = 0 with get, set
//    
//    /// Items already processed.
//    member val ProcessedItems = 0 with get, set
//    
//    /// Items which have failed processing.
//    member val FailedItems = 0 with get, set
//    
//    /// Overall status of the job.
//    member val Status = JobStatus.Initializing with get, set
//    
//    /// Any message that is associated with the job.
//    member val Message = "" with get, set
//    
//    override __.Validate() = ok()
//
///// Request to analyze a text against an analyzer. The reason to force
///// this parameter to request body is to avoid escaping of restricted characters
///// in the uri.
///// This is helpful during analyzer testing.
//[<ToString; Sealed>]
//type AnalysisRequest() = 
//    inherit ValidatableBase()
//    member val Text = Unchecked.defaultof<string> with get, set
//    override this.Validate() = this.Text |> notEmpty "Text"

type CreateResponse(id : string) = 
    member val Id = id with get, set

//type IndexStatusResponse() = 
//    member val Status = Unchecked.defaultof<IndexState> with get, set
type IndexExistsResponse() = 
    member val Exists = Unchecked.defaultof<bool> with get, set

module ServerSettings = 
    [<CLIMutableAttribute>]
    type T = 
        { HttpPort : int
          DataFolder : string
          PluginFolder : string
          ConfFolder : string
          NodeName : string }
        /// <summary>
        /// Get default server configuration
        /// </summary>
        static member GetDefault() = 
            let setting = 
                { HttpPort = 9800
                  DataFolder = Helpers.GenerateAbsolutePath("./data")
                  PluginFolder = Constants.PluginFolder
                  ConfFolder = Constants.ConfFolder
                  NodeName = "FlexSearchNode" }
            setting
    
    /// Reads server configuration from the given file
    let createFromFile (path : string, formatter : IFormatter) = 
        assert (String.IsNullOrWhiteSpace(path) <> true)
        if File.Exists(path) then 
            let fileStream = new FileStream(path, FileMode.Open)
            let parsedResult = formatter.DeSerialize<T>(fileStream)
            
            let setting = 
                { HttpPort = parsedResult.HttpPort
                  DataFolder = Helpers.GenerateAbsolutePath(parsedResult.DataFolder)
                  PluginFolder = Constants.PluginFolder
                  ConfFolder = Constants.ConfFolder
                  NodeName = parsedResult.NodeName }
            Choice1Of2(setting)
        else Choice2Of2(Errors.UNABLE_TO_PARSE_CONFIG |> GenerateOperationMessage)
