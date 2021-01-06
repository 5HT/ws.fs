namespace N2O

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Net.WebSockets
open System.Threading

// Pure MailboxProcessor-based WebSocket Server

[<AutoOpen>]
module Server =

    let runWorkers (tcp: TcpClient) (ctrl: MailboxProcessor<Sup>) (ct: CancellationToken) =
        MailboxProcessor.Start(
            (fun (inbox: MailboxProcessor<Payload>) ->
                async {
                    let ns = tcp.GetStream()
                    let size = tcp.ReceiveBufferSize
                    let bytes = Array.create size (byte 0)
                    let! len = ns.ReadAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
                    let lines = getLines bytes len
                    match (isWebSocketsUpgrade lines, wsResponse lines) with
                    | (true, upgrade) ->
                        do! ns.AsyncWrite upgrade
                        let ws = WebSocket.CreateFromStream(
                            (ns :> Stream), true, "n2o", TimeSpan(1, 0, 0))

                        ctrl.Post(Connect(inbox, ws))
                        Async.StartImmediate(runTelemetry ws inbox ct ctrl, ct)
                        Async.StartImmediate(runLoop ws size ct ctrl, ct)
                    | _ -> tcp.Close()
                }),
            cancellationToken = ct
        )

    let heartbeat (interval: int) (ct: CancellationToken) (ctrl: MailboxProcessor<Sup>) =
        async {
            while not ct.IsCancellationRequested do
                do! Async.Sleep interval
                ctrl.Post(Tick)
        }

    let acceptLoop (lst: TcpListener) (ct: CancellationToken) (ctrl: MailboxProcessor<Sup>) =
        async {
            try
                while not ct.IsCancellationRequested do
                    let! client = Async.FromBeginEnd(lst.BeginAcceptTcpClient, lst.EndAcceptTcpClient)
                    client.NoDelay <- true
                    runWorkers client ctrl ct |> ignore
            finally
                lst.Stop()
        }

    let runController (ct: CancellationToken) =
        MailboxProcessor.Start(
            (fun (inbox: MailboxProcessor<Sup>) ->
                let listeners = ResizeArray<_>()

                async {
                    while not ct.IsCancellationRequested do
                        let! msg = inbox.Receive()

                        match msg with
                        | Close ws -> printfn "Close: %A" ws
                        | Connect (l, ns) ->
                            printfn "Connect: %A %A" l ns
                            listeners.Add(l)
                        | Disconnect l ->
                            Console.WriteLine "Disconnect"
                            listeners.Remove(l) |> ignore
                        | Tick -> listeners.ForEach(fun l -> l.Post Ping)
                }),
            cancellationToken = ct
        )

    let supervisor (addr: string) (port: int) =
        let cts = new CancellationTokenSource()
        let token = cts.Token
        let controller = runController token
        let listener = TcpListener(IPAddress.Parse(addr), port)

        try
            listener.Start(10)
        with
        | :? SocketException -> failwithf "ERROR: %s/%i is using by another program" addr port
        | err -> failwithf "ERROR: %s" err.Message

        Async.StartImmediate(acceptLoop listener token controller, token)
        Async.StartImmediate(heartbeat 1000 token controller, token)
        { new IDisposable with
            member x.Dispose() = cts.Cancel() }

    let start (addr: string) (port: int) =
        use dispose = supervisor addr port
        Thread.Sleep -1