﻿module QueryFunctionTests

open FlexSearch.Core
open Swensen.Unquote
open System
open System.Collections.Generic

type ``Function Tests``(queryFunctionFactory : IFlexFactory<IFlexQueryFunction>) =
    let queryFunctions =
        let result = new Dictionary<string, IFlexQueryFunction>(StringComparer.OrdinalIgnoreCase)
        for pair in queryFunctionFactory.GetAllModules() do
            result.Add(pair.Value.GetType() |> getTypeNameFromAttribute, pair.Value)
        result

    let toSource givenList = match givenList with
                              | [] -> None
                              | list ->
                                  let dict = new Dictionary<string, string>()
                                  list |> Seq.iter (fun pair -> dict.Add pair)
                                  Some(dict)

    let isEqualTo expected sourceList inputText = 
        match ParseFunction inputText with
        | Ok(Func(fn, ps)) -> 
            let result = handleFunctionValue fn (ps |> Seq.toList) queryFunctions (toSource sourceList)
            test <@ result = ok expected @>
        | x -> raise <| invalidOp (sprintf "Couldn't parse to a function call. Received instead:\n%A" x)

    let fails sourceList inputText =
        match ParseFunction inputText with
        | Ok(Func(fn, ps)) -> 
            let result = handleFunctionValue fn (ps |> Seq.toList) queryFunctions (toSource sourceList)
            test <@ match result with Fail(_) -> true | _ -> false @>
        | x -> raise <| invalidOp (sprintf "Couldn't parse to a function call. Received instead:\n%A" x)

    member __.``Adding 1 to 2 should return 3``() =
        "add('1','2')" |> isEqualTo "3" []

    member __.``Query function names should be case insensitive``() =
        "Add('1','2')" |> isEqualTo "3" []

    member __.``Adding 1 to a numeric field should return field + 1``() =
        "add('1',field)" |> isEqualTo "3" ["field","2"]

    member __.``Adding 1 to a function that adds 1 to 1 should return 3``() =
        "add('1',add('1','1'))" |> isEqualTo "3" []
        
    member __.``Adding 3 four times should return 12``() =
        "add('3','3','3','3')" |> isEqualTo "12" []

    member __.``Adding -1 to a number should substract 1 from that number``() =
        "add('-1','5')" |> isEqualTo "4" []

    member __.``2 * 2 should return 4``() =
        "multiply('2','2')" |> isEqualTo "4" []

    member __.``A number multiplied by -1 should return that negative number``() =
        "multiply('-1','50')" |> isEqualTo "-50" []

    member __.``Max should return the maximum of the given numbers``() =
        "max('2','2','7','5','4')" |> isEqualTo "7" []

    member __.``Min should return the minimum of the given numbers``() =
        "min('3','2','7','5','4')" |> isEqualTo "2" []

    member __.``Avg should return the average of the given numbers``() =
        "avg('3','2','7','5','4')" |> isEqualTo "4.2" []

    member __.``Len should return the length of a given string``() =
        "len('this is sparta!!')" |> isEqualTo "16" []

    member __.``Len should return 0 for empty string``() =
        "len('')" |> isEqualTo "0" []

    member __.``Len should throw an error if more than one parameter is given``() =
        "len('some string', 'some other parameter')" |> fails []

    member __.``Upper should convert all characters from string to uppercase``() =
        "upper('this is sparta!!')" |> isEqualTo "THIS IS SPARTA!!" []

    member __.``Lower should convert all characters from string to lowercase``() =
        "lower('THIS IS NOT SPARTA!!')" |> isEqualTo "this is not sparta!!" []

    member __.``Substr(2,5) should get the first 5 characters starting from position 2``() =
        "substr('THIS IS SPARTA!!','2','5')" |> isEqualTo "IS IS" []