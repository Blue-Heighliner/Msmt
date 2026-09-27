namespace BlueHeighliner.Msmt.Tests.Integration;

/// <summary>Integration tests for <see cref="MsmtTlsChannel"/> over real loopback sockets.</summary>
public sealed class MsmtTlsChannelTests
{
    private readonly TimeSpan waitLimit = TimeSpan.FromSeconds(10);

    /// <summary>Data written on either side arrives intact on the other.</summary>
    [Fact]
    public async Task Write_EitherSide_ArrivesOnOtherSide()
    {
        (MsmtTlsChannel client, MsmtTlsChannel server) = await CreatePair();
        using (client)
        using (server)
        {
            await client.Write("ping"u8.ToArray(), CancellationToken.None);
            await server.Write("pong"u8.ToArray(), CancellationToken.None);

            Assert.Equal("ping", Encoding.ASCII.GetString(await ReadExactly(server, 4)));
            Assert.Equal("pong", Encoding.ASCII.GetString(await ReadExactly(client, 4)));
        }
    }

    /// <summary>A read that is waiting does not stop the same side from writing, the case BouncyCastle's blocking stream cannot handle.</summary>
    [Fact]
    public async Task Write_WhileReadIsWaiting_Succeeds()
    {
        (MsmtTlsChannel client, MsmtTlsChannel server) = await CreatePair();
        using (client)
        using (server)
        {
            Task<byte[]> waitingRead = ReadExactly(client, 5);
            await Task.Delay(300);

            await client.Write("hello"u8.ToArray(), CancellationToken.None);
            Assert.Equal("hello", Encoding.ASCII.GetString(await ReadExactly(server, 5)));

            await server.Write("world"u8.ToArray(), CancellationToken.None);
            Assert.Equal("world", Encoding.ASCII.GetString(await waitingRead));
        }
    }

    /// <summary>Both sides writing many messages at the same time, each while reading, lose and reorder nothing.</summary>
    [Fact]
    public async Task ConcurrentTraffic_BothDirections_ArrivesInOrderAndIntact()
    {
        (MsmtTlsChannel client, MsmtTlsChannel server) = await CreatePair();
        using (client)
        using (server)
        {
            const int messages = 300;
            byte[] Message(int index, int side) => [.. Enumerable.Range(0, 200 + index).Select(offset => (byte)(index + side + offset))];

            async Task Send(MsmtTlsChannel channel, int side)
            {
                for (int index = 0; index < messages; index++)
                {
                    await channel.Write(Message(index, side), CancellationToken.None);
                }
            }

            async Task<bool> Receive(MsmtTlsChannel channel, int side)
            {
                for (int index = 0; index < messages; index++)
                {
                    byte[] expected = Message(index, side);
                    if (!(await ReadExactly(channel, expected.Length)).AsSpan().SequenceEqual(expected))
                    {
                        return false;
                    }
                }

                return true;
            }

            Task<bool> serverReceives = Receive(server, 1);
            Task<bool> clientReceives = Receive(client, 2);
            await Task.WhenAll(Send(client, 1), Send(server, 2));

            Assert.True(await serverReceives.WaitAsync(waitLimit));
            Assert.True(await clientReceives.WaitAsync(waitLimit));
        }
    }

    /// <summary>A payload far larger than one TLS record arrives intact.</summary>
    [Fact]
    public async Task Write_LargePayload_ArrivesIntact()
    {
        (MsmtTlsChannel client, MsmtTlsChannel server) = await CreatePair();
        using (client)
        using (server)
        {
            byte[] payload = new byte[3 * 1024 * 1024];
            new Random(7).NextBytes(payload);

            Task<byte[]> reading = ReadExactly(server, payload.Length);
            await client.Write(payload, CancellationToken.None);

            Assert.True((await reading.WaitAsync(waitLimit)).AsSpan().SequenceEqual(payload));
        }
    }

    /// <summary>A key update while the other side is reading does not disturb the data that follows it.</summary>
    [Fact]
    public async Task Rekey_BetweenWrites_DataContinuesToArrive()
    {
        (MsmtTlsChannel client, MsmtTlsChannel server) = await CreatePair();
        using (client)
        using (server)
        {
            Task<byte[]> reading = ReadExactly(server, 6);
            await client.Write("one"u8.ToArray(), CancellationToken.None);
            client.Rekey();
            await client.Write("two"u8.ToArray(), CancellationToken.None);
            Assert.Equal("onetwo", Encoding.ASCII.GetString(await reading.WaitAsync(waitLimit)));

            await server.Write("back"u8.ToArray(), CancellationToken.None);
            Assert.Equal("back", Encoding.ASCII.GetString(await ReadExactly(client, 4)));
        }
    }

    /// <summary>Closing gracefully delivers what was queued, then the other side reads the end of the stream.</summary>
    [Fact]
    public async Task Close_AfterWrite_PeerReadsDataThenEnd()
    {
        (MsmtTlsChannel client, MsmtTlsChannel server) = await CreatePair();
        using (client)
        using (server)
        {
            await client.Write("last"u8.ToArray(), CancellationToken.None);
            await client.Close();

            Assert.Equal("last", Encoding.ASCII.GetString(await ReadExactly(server, 4)));
            Assert.Equal(0, await server.Read(new byte[1], CancellationToken.None));
            await client.Completion.WaitAsync(waitLimit);
        }
    }

    /// <summary>Aborting with a reason makes this side's pending and later reads and writes fail with it, and the other side sees the connection end.</summary>
    [Fact]
    public async Task Abort_WithReason_FaultsThisSideAndEndsOtherSide()
    {
        (MsmtTlsChannel client, MsmtTlsChannel server) = await CreatePair();
        using (client)
        using (server)
        {
            Task<int> pendingRead = client.Read(new byte[1], CancellationToken.None).AsTask();
            InvalidOperationException reason = new("boom");

            client.Abort(reason);

            Assert.Same(reason, await Assert.ThrowsAsync<InvalidOperationException>(() => pendingRead.WaitAsync(waitLimit)));
            IOException writeFailure = await Assert.ThrowsAsync<IOException>(() => client.Write("x"u8.ToArray(), CancellationToken.None));
            Assert.Same(reason, writeFailure.InnerException);
            Assert.True(client.IsClosed);

            try
            {
                Assert.Equal(0, await server.Read(new byte[1], CancellationToken.None));
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Cancelling a pending read does not close the channel, which stays usable.</summary>
    [Fact]
    public async Task Read_Cancelled_ChannelStaysUsable()
    {
        (MsmtTlsChannel client, MsmtTlsChannel server) = await CreatePair();
        using (client)
        using (server)
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.Read(new byte[1], cancellation.Token));

            await server.Write("still"u8.ToArray(), CancellationToken.None);
            Assert.Equal("still", Encoding.ASCII.GetString(await ReadExactly(client, 5)));
            Assert.False(client.IsClosed);
        }
    }

    /// <summary>A peer that stops reading makes a writer give up once sending has made no progress for the stall timeout, aborting with a timeout.</summary>
    [Fact]
    public async Task Write_PeerStopsReading_AbortsWithTimeoutAfterStall()
    {
        (MsmtTlsChannel client, MsmtTlsChannel server) = await CreatePair(TimeSpan.FromMilliseconds(500));
        using (client)
        using (server)
        {
            byte[] chunk = new byte[64 * 1024];
            IOException failure = await Assert.ThrowsAsync<IOException>(async () =>
            {
                while (true)
                {
                    await client.Write(chunk, CancellationToken.None);
                }
            }).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.IsType<TimeoutException>(failure.InnerException);
        }
    }

    /// <summary>A peer whose certificate is not trusted fails the handshake on both sides.</summary>
    [Fact]
    public async Task Handshake_UntrustedPeer_FailsOnBothSides()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, _) = TestMsmtCertificates.Create();
        (_, X509Certificate2Collection otherAuthorities) = TestMsmtCertificates.CreateExpiredServer();
        MsmtCredentials Credentials(X509Certificate2 identity) => new() { Identity = identity, TrustedAuthorities = otherAuthorities };

        (Socket clientSocket, Socket serverSocket, MsmtNameTarget target) = await ConnectSockets();
        MsmtTlsChannel client = new(clientSocket, null);
        MsmtTlsChannel server = new(serverSocket, null);
        using (client)
        using (server)
        {
            Task serverHandshake = server.AcceptAsServer(new MsmtTlsServer(Credentials(certificateB), false), CancellationToken.None);
            Task clientHandshake = client.ConnectAsClient(new MsmtTlsClient(Credentials(certificateA), target), CancellationToken.None);

            await Assert.ThrowsAnyAsync<Exception>(() => clientHandshake.WaitAsync(waitLimit));
            await Assert.ThrowsAnyAsync<Exception>(() => serverHandshake.WaitAsync(waitLimit));
        }
    }

    private async Task<(MsmtTlsChannel Client, MsmtTlsChannel Server)> CreatePair(TimeSpan? stallTimeout = null)
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        MsmtCredentials Credentials(X509Certificate2 identity) => new() { Identity = identity, TrustedAuthorities = trustedAuthorities };

        (Socket clientSocket, Socket serverSocket, MsmtNameTarget target) = await ConnectSockets();
        MsmtTlsChannel client = new(clientSocket, stallTimeout);
        MsmtTlsChannel server = new(serverSocket, stallTimeout);

        Task serverHandshake = server.AcceptAsServer(new MsmtTlsServer(Credentials(certificateB), false), CancellationToken.None);
        Task clientHandshake = client.ConnectAsClient(new MsmtTlsClient(Credentials(certificateA), target), CancellationToken.None);
        await Task.WhenAll(clientHandshake, serverHandshake).WaitAsync(waitLimit);
        return (client, server);
    }

    private async Task<(Socket Client, Socket Server, MsmtNameTarget Target)> ConnectSockets()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<Socket> accepting = listener.AcceptSocketAsync();
        Socket clientSocket = new(SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(IPAddress.Loopback, port);
        Socket serverSocket = await accepting;
        return (clientSocket, serverSocket, new MsmtNameTarget { Host = "127.0.0.1", Port = port, ServerName = "127.0.0.1" });
    }

    private async Task<byte[]> ReadExactly(MsmtTlsChannel channel, int count)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int chunk = await channel.Read(buffer.AsMemory(read), CancellationToken.None);
            Assert.NotEqual(0, chunk);
            read += chunk;
        }

        return buffer;
    }
}
