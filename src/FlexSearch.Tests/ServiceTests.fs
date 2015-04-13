﻿module ServiceTests

open FlexSearch.Core
open Swensen.Unquote

module IndexServiceTests = 
    type AddIndexTests() = 
        member __.``Should add a new index`` (index : Index.Dto, indexService : IIndexService) = 
            test <@ succeeded <| indexService.AddIndex(index) @>
        
        member __.``Newly created index should be online`` (indexService : IIndexService, index : Index.Dto) = 
            index.Online <- true
            test <@ succeeded <| indexService.AddIndex(index) @>
            test <@ indexService.GetIndexState(index.IndexName) = Choice1Of2(IndexState.Online) @>
        
        member __.``Newly created index should be offline`` (indexService : IIndexService, index : Index.Dto) = 
            index.Online <- false
            test <@ succeeded <| indexService.AddIndex(index) @>
            test <@ indexService.GetIndexState(index.IndexName) = Choice1Of2(IndexState.Offline) @>
        
        member __.``It is not possible to open an opened index`` (indexService : IIndexService, index : Index.Dto) = 
            index.Online <- true
            test <@ succeeded <| indexService.AddIndex(index) @>
            test <@ indexService.OpenIndex(index.IndexName) = Choice2Of2(IndexIsAlreadyOnline(index.IndexName)) @>
        
        member __.``It is not possible to close an closed index`` (indexService : IIndexService, index : Index.Dto) = 
            index.Online <- false
            test <@ succeeded <| indexService.AddIndex(index) @>
            test <@ indexService.CloseIndex(index.IndexName) = Choice2Of2(IndexIsAlreadyOffline(index.IndexName)) @>
        
        member __.``Can not create the same index twice`` (indexService : IIndexService, index : Index.Dto) = 
            index.Online <- false
            test <@ succeeded <| indexService.AddIndex(index) @>
            test <@ indexService.AddIndex(index) = Choice2Of2(IndexAlreadyExists(index.IndexName)) @>
        
        member __.``Offline index can be made online`` (indexService : IIndexService, index : Index.Dto) = 
            index.Online <- false
            test <@ succeeded <| indexService.AddIndex(index) @>
            test <@ succeeded <| indexService.OpenIndex(index.IndexName) @>
            test <@ indexService.GetIndexState(index.IndexName) = Choice1Of2(IndexState.Online) @>
        
        member __.``Online index can be made offline`` (indexService : IIndexService, index : Index.Dto) = 
            index.Online <- true
            test <@ succeeded <| indexService.AddIndex(index) @>
            test <@ succeeded <| indexService.CloseIndex(index.IndexName) @>
            test <@ indexService.GetIndexState(index.IndexName) = Choice1Of2(IndexState.Offline) @>

module DocumentServiceTests = 
    type DocumentManagementTests() = 
        
        member __.``Should be able to add and retrieve simple document`` (index : Index.Dto, documentId : string, 
                                                                          indexService : IIndexService, 
                                                                          documentService : IDocumentService) = 
            index.Online <- true
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, documentId)
            test <@ succeeded <| documentService.AddDocument(document) @>
            test <@ succeeded <| indexService.Refresh(index.IndexName) @>
            test <@ documentService.TotalDocumentCount(index.IndexName) = Choice1Of2(1) @>
            test <@ (extract <| documentService.GetDocument(index.IndexName, documentId)).Id = documentId @>
        
        member __.``Should be able to add and retrieve document after closing the index`` (index : Index.Dto, 
                                                                                           documentId : string, 
                                                                                           indexService : IIndexService, 
                                                                                           documentService : IDocumentService) = 
            index.Online <- true
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, documentId)
            test <@ succeeded <| documentService.AddDocument(document) @>
            test <@ succeeded <| indexService.Commit(index.IndexName) @>
            test <@ succeeded <| indexService.CloseIndex(index.IndexName) @>
            test <@ succeeded <| indexService.OpenIndex(index.IndexName) @>
            test <@ documentService.TotalDocumentCount(index.IndexName) = Choice1Of2(1) @>
            test <@ (extract <| documentService.GetDocument(index.IndexName, documentId)).Id = documentId @>
        
        member __.``Should be able to add and delete a document`` (index : Index.Dto, documentId : string, 
                                                                   indexService : IIndexService, 
                                                                   documentService : IDocumentService) = 
            index.Online <- true
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, documentId)
            test <@ succeeded <| documentService.AddDocument(document) @>
            test <@ succeeded <| indexService.Refresh(index.IndexName) @>
            test <@ documentService.TotalDocumentCount(index.IndexName) = Choice1Of2(1) @>
            test <@ (extract <| documentService.GetDocument(index.IndexName, documentId)).Id = documentId @>
            test <@ succeeded <| documentService.DeleteDocument(document.IndexName, documentId) @>
            test <@ succeeded <| indexService.Refresh(index.IndexName) @>
            test <@ extract <| documentService.TotalDocumentCount(index.IndexName) = 0 @>
            test <@ failed <| documentService.GetDocument(index.IndexName, documentId) @>

        member __.``Should be able to update a document`` (index : Index.Dto, documentId : string, 
                                                                   indexService : IIndexService, 
                                                                   documentService : IDocumentService) = 
            index.Online <- true
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, documentId)
            document.Fields.["t1"] <- "0"
            test <@ succeeded <| documentService.AddDocument(document) @>
            test <@ succeeded <| indexService.Refresh(index.IndexName) @>
            test <@ (extract <| documentService.GetDocument(index.IndexName, documentId)).Fields.["t1"] = document.Fields.["t1"] @>
            // Update the document
            document.Fields.["t1"] <- "1"
            test <@ succeeded <| documentService.AddOrUpdateDocument(document) @>
            test <@ succeeded <| indexService.Refresh(index.IndexName) @>
            test <@ (extract <| documentService.GetDocument(index.IndexName, documentId)).Fields.["t1"] = document.Fields.["t1"] @>
            