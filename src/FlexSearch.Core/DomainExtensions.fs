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
open FlexLucene.Codecs
open FlexLucene.Document
open FlexLucene.Index
open FlexLucene.Search
open FlexLucene.Search.Similarities
open FlexLucene.Store
open System
open System.Collections.Generic
open System.IO

// type mappings to avoid name conflict
type LuceneAnalyzer = FlexLucene.Analysis.Analyzer
type LuceneDocument = FlexLucene.Document.Document
type LuceneField = FlexLucene.Document.Field
type LuceneFieldType = FlexLucene.Document.FieldType

[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module FieldSimilarity = 
    open FlexLucene.Search.Similarities
    
    /// Converts the enum similarity to Lucene Similarity
    let getLuceneT = 
        function 
        | FieldSimilarity.TFIDF -> ok (new DefaultSimilarity() :> Similarity)
        | FieldSimilarity.BM25 -> ok (new BM25Similarity() :> Similarity)
        | unknown -> fail (UnSupportedSimilarity(unknown.ToString()))
    
    /// Default similarity provider used by FlexSearch
    [<SealedAttribute>]
    type Provider(mappings : IReadOnlyDictionary<string, Similarity>, defaultFormat : Similarity) = 
        inherit PerFieldSimilarityWrapper()
        override __.get (fieldName) = 
            match mappings.TryGetValue(fieldName) with
            | true, format -> format
            | _ -> defaultFormat

[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module DirectoryType = 
    /// Create a index directory from the given directory type    
    let getIndexDirectory (directoryType : DirectoryType, path : string) = 
        // Note: Might move to SingleInstanceLockFactory to provide other services to open
        // the index in read-only mode
        let lockFactory = NativeFSLockFactory.GetDefault()
        let file = (new java.io.File(path)).toPath()
        try 
            match directoryType with
            | DirectoryType.FileSystem -> ok (FSDirectory.Open(file, lockFactory) :> FlexLucene.Store.Directory)
            | DirectoryType.MemoryMapped -> ok (MMapDirectory.Open(file, lockFactory) :> FlexLucene.Store.Directory)
            | DirectoryType.Ram -> ok (new RAMDirectory() :> FlexLucene.Store.Directory)
            | unknown -> fail (UnsupportedDirectoryType(unknown.ToString()))
        with e -> fail (ErrorOpeningIndexWriter(path, exceptionPrinter (e), new ResizeArray<_>()))

[<RequireQualifiedAccessAttribute; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module IndexVersion = 
    open FlexLucene.Codecs
    open FlexLucene.Codecs.FlexSearch
    open FlexLucene.Util
    
    /// Build Lucene index version from FlexSearch index version    
    let build = 
        function 
        | IndexVersion.Lucene_4_x_x -> ok (Version.LUCENE_4_10_4)
        | IndexVersion.Lucene_5_0_0 -> ok (Version.LUCENE_5_0_0)
        | unknown -> fail (UnSupportedIndexVersion(unknown.ToString()))

///  Advance field properties to be used by custom field
type FieldIndexingInformation = 
    { Index : bool
      Tokenize : bool
      /// This maps to Lucene's term vectors and is only used for flex custom
      /// data type
      FieldTermVector : FieldTermVector
      /// This maps to Lucene's field index options
      FieldIndexOptions : FieldIndexOptions }

[<RequireQualifiedAccessAttribute; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module FieldType = 
    open FlexSearch.Core
    
    /// Represents the various data types supported by Flex
    type T = 
        | Stored
        | Custom of searchAnalyzer : LuceneAnalyzer * indexAnalyzer : LuceneAnalyzer * indexingInformation : FieldIndexingInformation
        | Highlight of searchAnalyzer : LuceneAnalyzer * indexAnalyzer : LuceneAnalyzer
        | Text of searchAnalyzer : LuceneAnalyzer * indexAnalyzer : LuceneAnalyzer
        | ExactText of analyzer : LuceneAnalyzer
        | Bool of analyzer : LuceneAnalyzer
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
    let inline requiresAnalyzer (f : T) = 
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
    let inline searchable (f : T) = 
        match f with
        | Stored(_) -> false
        | _ -> true
    
    /// Gets the default string value associated with the field type.
    let inline defaultValue (f : T) = 
        match f with
        | Custom(_, _, _) -> Constants.StringDefaultValue
        | Stored(_) -> Constants.StringDefaultValue
        | Text(_) -> Constants.StringDefaultValue
        | Bool(_) -> "false"
        | ExactText(_) -> Constants.StringDefaultValue
        | Date(_) -> "00010101"
        | DateTime(_) -> "00010101000000"
        | Int(_) -> "0"
        | Double(_) -> "0.0"
        | Highlight(_) -> Constants.StringDefaultValue
        | Long(_) -> "0"
    
    /// Gets the sort field associated with the field type. This is used for determining sort style
    let inline sortField (f : T) = 
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

[<RequireQualifiedAccessAttribute; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module IndexConfiguration = 
    let inline getIndexWriterConfiguration (codec : Codec) (similarity : Similarity) (indexAnalyzer : Analyzer) 
               (configuration : IndexConfiguration) = 
        let iwc = new IndexWriterConfig(indexAnalyzer)
        iwc.SetOpenMode(IndexWriterConfig.OpenMode.CREATE_OR_APPEND) |> ignore
        iwc.SetRAMBufferSizeMB(float configuration.RamBufferSizeMb) |> ignore
        iwc.SetCodec(codec).SetSimilarity(similarity) |> ignore
        iwc

[<RequireQualifiedAccessAttribute; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Field = 
    
    /// General Field which represents the basic properties for the field to be indexed
    type T = 
        { FieldName : string
          SchemaName : string
          IsStored : bool
          Similarity : FieldSimilarity
          FieldType : FieldType.T
          GenerateDocValue : bool
          Source : (Func<string, string, IReadOnlyDictionary<string, string>, string [], string> * string []) option
          /// Computed Information - Mostly helpers to avoid matching over Field type
          /// Helper property to determine if the field needs any analyzer.
          RequiresAnalyzer : bool
          /// Signifies if the field is searchable. Stored Field types are not
          /// searchable.
          Searchable : bool }
    
    /// Field info to be used by flex highlight field
    let flexHighLightFieldType = 
        lazy (let fieldType = new LuceneFieldType()
              fieldType.SetStored(true)
              fieldType.SetTokenized(true)
              fieldType.SetIndexOptions(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS_AND_OFFSETS)
              fieldType.Freeze()
              fieldType)
    
    /// Creates Lucene's field types. This is only used for FlexCustom data type to
    /// support flexible field type
    let getFieldTemplate (fieldTermVector : FieldTermVector, stored, tokenized, _) = 
        let fieldType = new LuceneFieldType()
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
    
    let store = LuceneField.Store.YES
    let doNotStore = LuceneField.Store.NO
    
    /// A field that is indexed but not tokenized: the entire String value is indexed as a single token. 
    /// For example this might be used for a 'country' field or an 'id' field, or any field that you 
    /// intend to use for sorting or access through the field cache.
    let getStringField (fieldName, value: string, store : LuceneField.Store) = new StringField(fieldName, value, store) :> LuceneField
    
    /// A field that is indexed and tokenized, without term vectors. For example this would be used on a 
    /// 'body' field, that contains the bulk of a document's text.
    let getTextField (fieldName, value, store) = new TextField(fieldName, value, store) :> LuceneField
    
    let getLongField (fieldName, value : int64, store : LuceneField.Store) = 
        new LongField(fieldName, value, store) :> LuceneField
    let getIntField (fieldName, value : int32, store : LuceneField.Store) = 
        new IntField(fieldName, value, store) :> LuceneField
    let getDoubleField (fieldName, value : float, store : LuceneField.Store) = 
        new DoubleField(fieldName, value, store) :> LuceneField
    let getStoredField (fieldName, value : string) = new StoredField(fieldName, value) :> LuceneField
    let getField (fieldName, value : string, template : FlexLucene.Document.FieldType) = 
        new LuceneField(fieldName, value, template)
    let bytesForNullString = System.Text.Encoding.Unicode.GetBytes(Constants.StringDefaultValue)
    
    /// Set the value of index field to the default value
    let inline updateLuceneFieldToDefault flexField (isDocValue : bool) (luceneField : LuceneField) = 
        match flexField.FieldType with
        | FieldType.T.Custom(_, _, _) -> luceneField.SetStringValue(Constants.StringDefaultValue)
        | FieldType.T.Stored -> luceneField.SetStringValue(Constants.StringDefaultValue)
        | FieldType.T.Text(_) -> luceneField.SetStringValue(Constants.StringDefaultValue)
        | FieldType.T.Bool(_) -> luceneField.SetStringValue("false")
        | FieldType.T.ExactText(_) -> 
            if isDocValue then luceneField.SetBytesValue(bytesForNullString)
            else luceneField.SetStringValue(Constants.StringDefaultValue)
        | FieldType.T.Highlight(_) -> luceneField.SetStringValue(Constants.StringDefaultValue)
        | FieldType.T.Date -> luceneField.SetLongValue(DateDefaultValue)
        | FieldType.T.DateTime -> luceneField.SetLongValue(DateTimeDefaultValue)
        | FieldType.T.Int -> 
            // Numeric doc values can only be saved as Int64 
            if isDocValue then luceneField.SetLongValue(0L)
            else luceneField.SetIntValue(0)
        | FieldType.T.Double -> luceneField.SetDoubleValue(0.0)
        | FieldType.T.Long -> luceneField.SetLongValue(int64 0)
    
    /// Set the value of index field using the passed value
    let inline updateLuceneField flexField (lucenceField : LuceneField) (isDocValue : bool) (value : string) = 
        if isBlank value then lucenceField |> updateLuceneFieldToDefault flexField isDocValue
        else 
            match flexField.FieldType with
            | FieldType.T.Custom(_, _, _) -> lucenceField.SetStringValue(value)
            | FieldType.T.Stored -> lucenceField.SetStringValue(value)
            | FieldType.T.Text(_) -> lucenceField.SetStringValue(value)
            | FieldType.T.Highlight(_) -> lucenceField.SetStringValue(value)
            | FieldType.T.ExactText(_) -> 
                if isDocValue then lucenceField.SetBytesValue(System.Text.Encoding.Unicode.GetBytes(value))
                else lucenceField.SetStringValue(value)
            | FieldType.T.Bool(_) -> (value |> pBool false).ToString() |> lucenceField.SetStringValue
            | FieldType.T.Date -> (value |> pLong DateDefaultValue) |> lucenceField.SetLongValue
            | FieldType.T.DateTime -> (value |> pLong DateTimeDefaultValue) |> lucenceField.SetLongValue
            | FieldType.T.Int -> 
                // Numeric doc values can only be saved as Int64 
                if isDocValue then (value |> pLong 0L) |> lucenceField.SetLongValue
                else (value |> pInt 0) |> lucenceField.SetIntValue
            | FieldType.T.Double -> (value |> pDouble 0.0) |> lucenceField.SetDoubleValue
            | FieldType.T.Long -> (value |> pLong 0L) |> lucenceField.SetLongValue
    
    let inline storeInfoMap (isStored) = 
        if isStored then LuceneField.Store.YES
        else LuceneField.Store.NO
    
    /// Create docvalues field from 
    let inline createDocValueField flexField = 
        match flexField.FieldType with
        | FieldType.T.Custom(_) | FieldType.T.Stored | FieldType.T.Text(_) | FieldType.T.Highlight(_) | FieldType.T.Bool(_) -> 
            None
        | FieldType.T.ExactText(_) -> 
            Some <| (new SortedDocValuesField(flexField.SchemaName, new FlexLucene.Util.BytesRef()) :> LuceneField)
        | FieldType.T.Long | FieldType.T.DateTime | FieldType.T.Date | FieldType.T.Int -> 
            Some <| (new NumericDocValuesField(flexField.SchemaName, 0L) :> LuceneField)
        | FieldType.T.Double -> Some <| (new DoubleDocValuesField(flexField.SchemaName, 0.0) :> LuceneField)
    
    /// Create docvalues field from 
    let inline requiresCustomDocValues (fieldType : FieldType.T) = 
        match fieldType with
        | FieldType.T.Custom(_) | FieldType.T.Stored | FieldType.T.Text(_) | FieldType.T.Highlight(_) | FieldType.T.Bool(_) -> 
            false
        | FieldType.T.ExactText(_) | FieldType.T.Long | FieldType.T.DateTime | FieldType.T.Date | FieldType.T.Int | FieldType.T.Double -> 
            true
    
    /// Creates a default Lucene index field for the passed flex field.
    let inline createDefaultLuceneField flexField = 
        let storeInfo = storeInfoMap (flexField.IsStored)
        match flexField.FieldType with
        | FieldType.T.Custom(_, _, b) -> 
            getField 
                (flexField.SchemaName, Constants.StringDefaultValue, 
                 getFieldTemplate (b.FieldTermVector, flexField.IsStored, b.Tokenize, b.Index))
        | FieldType.T.Stored -> getStoredField (flexField.SchemaName, Constants.StringDefaultValue)
        | FieldType.T.Text(_) -> 
            getTextField (flexField.SchemaName, Constants.StringDefaultValue, storeInfoMap (flexField.IsStored))
        | FieldType.T.Highlight(_) -> 
            getField (flexField.SchemaName, Constants.StringDefaultValue, flexHighLightFieldType.Value)
        | FieldType.T.ExactText(_) -> getTextField (flexField.SchemaName, Constants.StringDefaultValue, storeInfo)
        | FieldType.T.Bool(_) -> getTextField (flexField.SchemaName, "false", storeInfo)
        | FieldType.T.Date -> getLongField (flexField.SchemaName, DateDefaultValue, storeInfo)
        | FieldType.T.DateTime -> getLongField (flexField.SchemaName, DateTimeDefaultValue, storeInfo)
        | FieldType.T.Int -> getIntField (flexField.SchemaName, 0, storeInfo)
        | FieldType.T.Double -> getDoubleField (flexField.SchemaName, 0.0, storeInfo)
        | FieldType.T.Long -> getLongField (flexField.SchemaName, int64 0, storeInfo)
    
    /// Get a search query parser associated with the field 
    let inline getSearchAnalyzer (flexField : T) = 
        match flexField.FieldType with
        | FieldType.T.Custom(a, _, _) -> Some(a)
        | FieldType.T.Highlight(a, _) -> Some(a)
        | FieldType.T.Text(a, _) -> Some(a)
        | FieldType.T.ExactText(a) -> Some(a)
        | FieldType.T.Bool(a) -> Some(a)
        | FieldType.T.Date | FieldType.T.DateTime | FieldType.T.Int | FieldType.T.Double | FieldType.T.Stored | FieldType.T.Long -> 
            None
    
    let create (fieldName : string, fieldType : FieldType.T, generateDocValues) = 
        { FieldName = fieldName
          SchemaName = fieldName
          IsStored = true
          FieldType = fieldType
          GenerateDocValue = generateDocValues
          Source = None
          Searchable = FieldType.searchable (fieldType)
          Similarity = FieldSimilarity.TFIDF
          RequiresAnalyzer = FieldType.requiresAnalyzer (fieldType) }
    
    /// Field to be used by the Id field
    let getIdField (bloomEnabled) = 
        let indexInformation = 
            { Index = true
              Tokenize = false
              FieldTermVector = FieldTermVector.DoNotStoreTermVector
              FieldIndexOptions = FieldIndexOptions.DocsOnly }
        create 
            (Constants.IdField, 
             FieldType.Custom(CaseInsensitiveKeywordAnalyzer, CaseInsensitiveKeywordAnalyzer, indexInformation), false)
    
    /// Field to be used by time stamp
    let getTimeStampField() = create (Constants.LastModifiedField, FieldType.DateTime, false)
    
    /// Build FlexField from field
    let build (field : FlexSearch.Core.Field, indexConfiguration : IndexConfiguration, 
               analyzerFactory : string -> Result<FlexLucene.Analysis.Analyzer>, scriptService) = 
        let getSource (field : FlexSearch.Core.Field) = 
            if (String.IsNullOrWhiteSpace(field.ScriptName)) then ok <| None
            else 
                match scriptService (field.ScriptName) with
                | Ok(func) -> ok <| Some(func)
                | _ -> fail <| ScriptNotFound(field.ScriptName, field.FieldName)
        
        let getFieldType (field : FlexSearch.Core.Field) = 
            maybe { 
                match field.FieldType with
                | FieldDataType.Int -> return FieldType.T.Int
                | FieldDataType.Double -> return FieldType.T.Double
                | FieldDataType.Bool -> return FieldType.T.Bool(CaseInsensitiveKeywordAnalyzer)
                | FieldDataType.Date -> return FieldType.T.Date
                | FieldDataType.DateTime -> return FieldType.T.DateTime
                | FieldDataType.Long -> return FieldType.T.Long
                | FieldDataType.Stored -> return FieldType.T.Stored
                | FieldDataType.ExactText -> return FieldType.T.ExactText(CaseInsensitiveKeywordAnalyzer)
                | FieldDataType.Text | FieldDataType.Highlight | FieldDataType.Custom -> 
                    let! searchAnalyzer = analyzerFactory <| field.SearchAnalyzer
                    let! indexAnalyzer = analyzerFactory <| field.IndexAnalyzer
                    match field.FieldType with
                    | FieldDataType.Text -> return FieldType.T.Text(searchAnalyzer, indexAnalyzer)
                    | FieldDataType.Highlight -> return FieldType.T.Highlight(searchAnalyzer, indexAnalyzer)
                    | FieldDataType.Custom -> 
                        let indexingInformation = 
                            { Index = field.Index
                              Tokenize = field.Analyze
                              FieldTermVector = field.TermVector
                              FieldIndexOptions = field.IndexOptions }
                        return FieldType.T.Custom(searchAnalyzer, indexAnalyzer, indexingInformation)
                    | _ -> return! fail (AnalyzerNotSupportedForFieldType(field.FieldName, field.FieldType.ToString()))
                | _ -> return! fail (UnSupportedFieldType(field.FieldName, field.FieldType.ToString()))
            }
        
        let checkDocValuesSupport (field : FlexSearch.Core.Field) (fieldType : FieldType.T) = 
            field.AllowSort && requiresCustomDocValues (fieldType)
        maybe { 
            let! source = getSource (field)
            let! fieldType = getFieldType (field)
            return { FieldName = field.FieldName
                     SchemaName = field.FieldName
                     FieldType = fieldType
                     GenerateDocValue = checkDocValuesSupport field fieldType
                     Source = source
                     Similarity = field.Similarity
                     IsStored = field.Store
                     Searchable = FieldType.searchable (fieldType)
                     RequiresAnalyzer = FieldType.requiresAnalyzer (fieldType) }
        }

[<RequireQualifiedAccessAttribute; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module SearchQuery = 
    /// Gets a search query from an Owin request using the optional body
    let getQueryFromRequest request body = 
        let query = 
            match body with
            | Some(q) -> q
            | None -> new SearchQuery()
        query.QueryString <- request.OwinContext |> stringFromQueryString "q" query.QueryString
        query.Columns <- match request.OwinContext.Request.Query.Get("c") with
                         | null -> query.Columns
                         | v -> v.Split([| ',' |], System.StringSplitOptions.RemoveEmptyEntries)
        query.Count <- request.OwinContext |> intFromQueryString "count" query.Count
        query.Skip <- request.OwinContext |> intFromQueryString "skip" query.Skip
        query.OrderBy <- request.OwinContext |> stringFromQueryString "orderby" query.OrderBy
        query.OrderByDirection <- request.OwinContext |> stringFromQueryString "orderbydirection" query.OrderByDirection
        query.ReturnFlatResult <- request.OwinContext |> boolFromQueryString "returnflatresult" query.ReturnFlatResult
        query.SearchProfile <- request.OwinContext |> stringFromQueryString "searchprofile" query.SearchProfile
        query.IndexName <- request.ResId.Value
        query

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
            ok setting
        else fail <| FileNotFound path
