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
            test <@ indexService.GetIndexState(index.IndexName) = Choice1Of2(IndexStatus.Online) @>
        
        member __.``Newly created index should be offline`` (indexService : IIndexService, index : Index.Dto) = 
            index.Online <- false
            test <@ succeeded <| indexService.AddIndex(index) @>
            test <@ indexService.GetIndexState(index.IndexName) = Choice1Of2(IndexStatus.Offline) @>
        
        member __.``It is not possible to open an opened index`` (indexService : IIndexService, index : Index.Dto) = 
            index.Online <- true
            test <@ succeeded <| indexService.AddIndex(index) @>
            test <@ indexService.OpenIndex(index.IndexName) = fail(IndexIsAlreadyOnline(index.IndexName)) @>
        
        member __.``It is not possible to close an closed index`` (indexService : IIndexService, index : Index.Dto) = 
            index.Online <- false
            test <@ succeeded <| indexService.AddIndex(index) @>
            test <@ indexService.CloseIndex(index.IndexName) = fail(IndexIsAlreadyOffline(index.IndexName)) @>
        
        member __.``Can not create the same index twice`` (indexService : IIndexService, index : Index.Dto) = 
            index.Online <- false
            test <@ succeeded <| indexService.AddIndex(index) @>
            test <@ indexService.AddIndex(index) = fail(IndexAlreadyExists(index.IndexName)) @>
        
        member __.``Offline index can be made online`` (indexService : IIndexService, index : Index.Dto) = 
            index.Online <- false
            test <@ succeeded <| indexService.AddIndex(index) @>
            test <@ succeeded <| indexService.OpenIndex(index.IndexName) @>
            test <@ indexService.GetIndexState(index.IndexName) = Choice1Of2(IndexStatus.Online) @>
        
        member __.``Online index can be made offline`` (indexService : IIndexService, index : Index.Dto) = 
            index.Online <- true
            test <@ succeeded <| indexService.AddIndex(index) @>
            test <@ succeeded <| indexService.CloseIndex(index.IndexName) @>
            test <@ indexService.GetIndexState(index.IndexName) = Choice1Of2(IndexStatus.Offline) @>

    type CommonTests() =
        member __.``Should return size of existing index`` (indexService: IIndexService, index : Index.Dto) =
            index.Online <- true
            test <@ succeeded <| indexService.AddIndex(index) @>
            test <@ succeeded <| indexService.Commit(index.IndexName) @>
            test <@ succeeded <| indexService.GetDiskUsage index.IndexName @>

        member __.``Should fail when asking for size of non-existing index`` (indexService: IIndexService) =
            test <@ failed <| indexService.GetDiskUsage "non-existing-index" @>

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
        
        member __.``Should be able to update a document`` (index : Index.Dto, id : string, indexService : IIndexService, 
                                                           documentService : IDocumentService) = 
            index.Online <- true
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, id)
            document.Fields.["t1"] <- "0"
            test <@ succeeded <| documentService.AddDocument(document) @>
            test <@ succeeded <| indexService.Refresh(index.IndexName) @>
            test 
                <@ (extract <| documentService.GetDocument(index.IndexName, id)).Fields.["t1"] = document.Fields.["t1"] @>
            // Update the document
            document.Fields.["t1"] <- "1"
            test <@ succeeded <| documentService.AddOrUpdateDocument(document) @>
            test <@ succeeded <| indexService.Refresh(index.IndexName) @>
            test 
                <@ (extract <| documentService.GetDocument(index.IndexName, id)).Fields.["t1"] = document.Fields.["t1"] @>
    
    type ``Versioning tests``() = 
        
        member __.``Cannot create a duplicate document with a timestamp of -1`` (indexService : IIndexService, 
                                                                                 documentService : IDocumentService, 
                                                                                 index : Index.Dto) = 
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, "1")
            test <@ succeeded <| documentService.AddDocument(document) @>
            document.TimeStamp <- -1L
            test <@ documentService.AddDocument(document) = fail(DocumentIdAlreadyExists(index.IndexName, "1")) @>
        
        member __.``Cannot create a duplicate document with a timestamp of -1 even after cache is cleared`` (indexService : IIndexService, 
                                                                                                             documentService : IDocumentService, 
                                                                                                             index : Index.Dto) = 
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, "1")
            test <@ succeeded <| documentService.AddDocument(document) @>
            test <@ succeeded <| indexService.Refresh(index.IndexName) @>
            document.TimeStamp <- -1L
            test <@ documentService.AddDocument(document) = fail(DocumentIdAlreadyExists(index.IndexName, "1")) @>
        
        member __.``Cannot create a document with timestamp of 1`` (indexService : IIndexService, 
                                                                    documentService : IDocumentService, 
                                                                    index : Index.Dto) = 
            // TimeStamp of 1 implies that we want to ensure that the document exists which is against the logic of basic create operation
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, "1", TimeStamp = 1L)
            test <@ failed <| documentService.AddDocument(document) @>
        
        member __.``Duplicate document can be created with a timestamp of 0`` (indexService : IIndexService, 
                                                                               documentService : IDocumentService, 
                                                                               index : Index.Dto) = 
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, "1", TimeStamp = 0L)
            test <@ succeeded <| documentService.AddDocument(document) @>
            document.TimeStamp <- 0L
            test <@ succeeded <| documentService.AddDocument(document) @>
            test <@ extract <| documentService.TotalDocumentCount(index.IndexName) = 2 @>
        
        member __.``For optimistic update the timestamp should match`` (indexService : IIndexService, 
                                                                        documentService : IDocumentService, 
                                                                        index : Index.Dto) = 
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, "1")
            test <@ succeeded <| documentService.AddDocument(document) @>
            test <@ succeeded <| indexService.Refresh(index.IndexName) @>
            test <@ succeeded <| documentService.AddOrUpdateDocument(document) @>
            document.TimeStamp <- 1000L
            test <@ failed <| documentService.AddOrUpdateDocument(document) @>
        
        member __.``Cannot update a document with wrong timestamp`` (indexService : IIndexService, 
                                                                     documentService : IDocumentService, 
                                                                     index : Index.Dto) = 
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, "1")
            test <@ succeeded <| documentService.AddDocument(document) @>
            document.TimeStamp <- 1000L
            test <@ failed <| documentService.AddOrUpdateDocument(document) @>
        
        member __.``Document should exist when updating with a timestamp of 1`` (indexService : IIndexService, 
                                                                                 documentService : IDocumentService, 
                                                                                 index : Index.Dto) = 
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, "1")
            test <@ succeeded <| documentService.AddDocument(document) @>
            document.TimeStamp <- 1L
            test <@ succeeded <| documentService.AddOrUpdateDocument(document) @>
        
        member __.``Cannot create a document using update operation with a timestamp of 1`` (indexService : IIndexService, 
                                                                                             documentService : IDocumentService, 
                                                                                             index : Index.Dto) = 
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, "1", TimeStamp = 1L)
            test 
                <@ documentService.AddOrUpdateDocument(document) = fail
                                                                       (DocumentIdNotFound(index.IndexName, document.Id)) @>
        
        member __.``A newly created document should have version number greater than 1`` (indexService : IIndexService, 
                                                                                          documentService : IDocumentService, 
                                                                                          index : Index.Dto) = 
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, "1")
            test <@ succeeded <| documentService.AddDocument(document) @>
            test <@ succeeded <| indexService.Refresh(index.IndexName) @>
            test <@ (extract <| documentService.GetDocument(index.IndexName, document.Id)).TimeStamp > 1L @>
        
        member __.``Timestamp field can be correctly retieved from the physical medium`` (indexService : IIndexService, 
                                                                                          documentService : IDocumentService, 
                                                                                          index : Index.Dto) = 
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, "1")
            test <@ succeeded <| documentService.AddDocument(document) @>
            test <@ succeeded <| indexService.Commit(index.IndexName) @>
            test <@ succeeded <| indexService.CloseIndex(index.IndexName) @>
            test <@ succeeded <| indexService.OpenIndex(index.IndexName) @>
            test <@ (extract <| documentService.GetDocument(index.IndexName, document.Id)).TimeStamp > 1L @>
        
        member __.``Fields returned by the document service should match the total number of fields in the index`` (indexService : IIndexService, 
                                                                                                                    documentService : IDocumentService, 
                                                                                                                    index : Index.Dto) = 
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, "1")
            test <@ succeeded <| documentService.AddDocument(document) @>
            test <@ succeeded <| indexService.Refresh(index.IndexName) @>
            test <@ succeeded <| indexService.CloseIndex(index.IndexName) @>
            test <@ succeeded <| indexService.OpenIndex(index.IndexName) @>
            test 
                <@ (extract <| documentService.GetDocument(index.IndexName, document.Id)).Fields.Count = index.Fields.Length @>
        
        member __.``Version Cache gets cleared after a refresh is called`` (indexService : IIndexService, 
                                                                            documentService : IDocumentService, 
                                                                            index : Index.Dto) = 
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, "1")
            test <@ succeeded <| documentService.AddDocument(document) @>
            test <@ succeeded <| indexService.Refresh(index.IndexName) @>
            let indexWriter = extract <| indexService.IsIndexOnline(index.IndexName)
            test <@ indexWriter.Caches.[0].Current.Count = 0 @>
        
        member __.``Document version can be reterieved even after all caches are cleared`` (indexService : IIndexService, 
                                                                                            documentService : IDocumentService, 
                                                                                            index : Index.Dto) = 
            test <@ succeeded <| indexService.AddIndex(index) @>
            let document = new Document.Dto(index.IndexName, "1")
            test <@ succeeded <| documentService.AddDocument(document) @>
            test <@ succeeded <| indexService.Refresh(index.IndexName) @>
            let indexWriter = extract <| indexService.IsIndexOnline(index.IndexName)
            indexWriter.Caches.[0].Current.Clear()
            indexWriter.Caches.[0].Old.Clear()
            // As all the caches are cleared the index has to load the documment version from the
            // docvalues. For optimistic update to work the timestamp has to match 
            test <@ succeeded <| documentService.AddOrUpdateDocument(document) @>

type QueueServiceTests() = 
    member __.``Queue service can be used to add document to an index`` (indexService : IIndexService, 
                                                                         queueService : IQueueService, 
                                                                         documentService : IDocumentService, 
                                                                         index : Index.Dto) = 
        test <@ succeeded <| indexService.AddIndex(index) @>
        let document = new Document.Dto(index.IndexName, "1")
        queueService.AddDocumentQueue(document)
        test <@ succeeded <| indexService.Refresh(index.IndexName) @>
        test <@ extract <| documentService.TotalDocumentCount(index.IndexName) = 1 @>
        
