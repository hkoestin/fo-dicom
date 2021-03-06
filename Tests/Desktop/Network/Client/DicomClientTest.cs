﻿// Copyright (c) 2012-2019 fo-dicom contributors.
// Licensed under the Microsoft Public License (MS-PL).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dicom.Helpers;
using Dicom.Log;
using Dicom.Network.Client.States;
using Xunit;
using Xunit.Abstractions;

namespace Dicom.Network.Client
{
    [Collection("Network"), Trait("Category", "Network")]
    public class DicomClientTest
    {
        private readonly ITestOutputHelper _testOutputHelper;
        private readonly XUnitDicomLogger _logger;

        #region Fields

        private static string remoteHost;

        private static int remotePort;

        #endregion

        #region Constructors

        public DicomClientTest(ITestOutputHelper testOutputHelper)
        {
            _testOutputHelper = testOutputHelper;
            _logger = new XUnitDicomLogger(testOutputHelper)
                .IncludeTimestamps()
                .IncludeThreadId()
                .WithMinimumLevel(LogLevel.Debug);
            remoteHost = null;
            remotePort = 0;
        }

        #endregion

        #region Helper functions

        private IDicomServer CreateServer<T>(int port) where T : DicomService, IDicomServiceProvider
        {
            var server = DicomServer.Create<T>(port);
            server.Logger = _logger.IncludePrefix(nameof(IDicomServer));
            return server;
        }

        private TServer CreateServer<TProvider, TServer>(int port)
            where TProvider : DicomService, IDicomServiceProvider
            where TServer : class, IDicomServer<TProvider>, new()
        {
            var logger = _logger.IncludePrefix(nameof(IDicomServer));
            var options = new DicomServiceOptions
            {
                LogDimseDatasets = false,
                LogDataPDUs = false,
            };
            var ipAddress = NetworkManager.IPv4Any;
            var server = DicomServer.Create<TProvider, TServer>(ipAddress, port, logger: logger, options: options);
            return server as TServer;
        }

        private DicomClient CreateClient(string host, int port, bool useTls, string callingAe, string calledAe)
        {
            var client = new DicomClient(host, port, useTls, callingAe, calledAe)
            {
                Logger = _logger.IncludePrefix(typeof(DicomClient).Name)
            };
            return client;
        }

        #endregion

        [Fact]
        public async Task Send_SingleRequest_Recognized()
        {
            int port = Ports.GetNext();
            using (DicomServer.Create<DicomCEchoProvider>(port))
            {
                var counter = 0;
                var request = new DicomCEchoRequest {OnResponseReceived = (req, res) => Interlocked.Increment(ref counter)};

                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                client.AddRequest(request);

                await client.SendAsync();

                Assert.Equal(1, counter);
            }
        }

        [Theory]
        [InlineData(2)]
        [InlineData(20)]
        [InlineData(100)]
        [InlineData(1000)]
        public async Task Send_MultipleRequests_AllRecognized(int expected)
        {
            var port = Ports.GetNext();
            var flag = new ManualResetEventSlim();

            using (CreateServer<DicomCEchoProvider>(port))
            {
                var actual = 0;
                DicomCEchoRequest.ResponseDelegate callback = (req, res) =>
                {
                    Interlocked.Increment(ref actual);
                    if (actual == expected) flag.Set();
                };

                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                client.NegotiateAsyncOps(expected, 1);

                for (var i = 0; i < expected; ++i)
                    client.AddRequest(new DicomCEchoRequest {OnResponseReceived = callback});

                await client.SendAsync();
                flag.Wait(10000);

                Assert.Equal(expected, actual);
            }
        }

        [Theory]
        [InlineData(20)]
        [InlineData(100)]
        public async Task Send_MultipleTimes_AllRecognized(int expected)
        {
            var port = Ports.GetNext();
            var flag = new ManualResetEventSlim();
            var logger = _logger.IncludePrefix("UnitTest");
            var sendTasks = new List<Task>();
            using (var server = CreateServer<DicomCEchoProvider>(port))
            {
                while (!server.IsListening) Thread.Sleep(50);

                var actual = 0;

                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");

                for (var i = 0; i < expected; ++i)
                {
                    var localIndex = i;
                    client.AddRequest(
                        new DicomCEchoRequest
                        {
                            OnResponseReceived = (req, res) =>
                            {
                                logger.Info($"i = {localIndex}, received response for [{req.MessageID}]");
                                Interlocked.Increment(ref actual);
                                if (actual == expected) flag.Set();
                            }
                        });
                    sendTasks.Add(client.SendAsync());
                }

                await Task.WhenAll(sendTasks).ConfigureAwait(false);
                flag.Wait(10000);
                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public async Task SendAsync_SingleRequest_Recognized()
        {
            int port = Ports.GetNext();
            using (CreateServer<DicomCEchoProvider>(port))
            {
                var counter = 0;
                var request = new DicomCEchoRequest {OnResponseReceived = (req, res) => Interlocked.Increment(ref counter)};

                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                client.AddRequest(request);

                var task = client.SendAsync();
                //await Task.WhenAny(task, Task.Delay(10000));
                await task;
                Assert.Equal(1, counter);
            }
        }

        [Theory]
        [InlineData(2)]
        [InlineData(20)]
        [InlineData(100)]
        [InlineData(1000)]
        public async Task SendAsync_MultipleRequests_AllRecognized(int expected)
        {
            int port = Ports.GetNext();
            using (CreateServer<DicomCEchoProvider>(port))
            {
                var actual = 0;

                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                client.NegotiateAsyncOps(expected, 1);

                for (var i = 0; i < expected; ++i)
                    client.AddRequest(new DicomCEchoRequest {OnResponseReceived = (req, res) => Interlocked.Increment(ref actual)});

                var task = client.SendAsync();
                await Task.WhenAny(task, Task.Delay(30000));

                Assert.Equal(expected, actual);
            }
        }

        [Theory]
        [InlineData(20)]
        [InlineData(100)]
        public async Task SendAsync_MultipleTimes_AllRecognized(int expected)
        {
            var port = Ports.GetNext();
            var flag = new ManualResetEventSlim();

            using (var server = CreateServer<DicomCEchoProvider>(port))
            {
                while (!server.IsListening) await Task.Delay(50);

                var actual = 0;

                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                for (var i = 0; i < expected; i++)
                {
                    client.AddRequest(
                        new DicomCEchoRequest
                        {
                            OnResponseReceived = (req, res) =>
                            {
                                Interlocked.Increment(ref actual);
                                if (actual == expected) flag.Set();
                            }
                        });
                    await client.SendAsync();
                }

                flag.Wait(10000);
                Assert.Equal(expected, actual);
            }
        }

        [Theory]
        [InlineData(20)]
        [InlineData(200)]
        public async Task SendAsync_MultipleTimesParallel_AllRecognized(int expected)
        {
            int port = Ports.GetNext();

            using (
                var server = CreateServer<DicomCEchoProvider>(port))
            {
                await Task.Delay(500);
                Assert.True(server.IsListening, "Server is not listening");

                var actual = 0;

                var requests = Enumerable.Range(0, expected).Select(
                    async requestIndex =>
                    {
                        var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                        client.Logger = _logger.IncludePrefix($"{nameof(DicomClient)} #{requestIndex}");
                        client.AddRequest(
                            new DicomCEchoRequest
                            {
                                OnResponseReceived = (req, res) =>
                                {
                                    _testOutputHelper.WriteLine("Response #{0}", requestIndex);
                                    Interlocked.Increment(ref actual);
                                }
                            });

                        _testOutputHelper.WriteLine("Sending #{0}", requestIndex);
                        await client.SendAsync();
                        _testOutputHelper.WriteLine("Sent (or timed out) #{0}", requestIndex);
                    }).ToList();
                await Task.WhenAll(requests);

                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public async Task AssociationAccepted_SuccessfulSend_IsInvoked()
        {
            var port = Ports.GetNext();
            using (CreateServer<MockCEchoProvider>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");

                var accepted = false;
                client.AssociationAccepted += (sender, args) => accepted = true;

                client.AddRequest(new DicomCEchoRequest());
                await client.SendAsync();

                Assert.True(accepted);
            }
        }

        [Fact]
        public async Task AssociationRejected_AssociationNotAllowed_IsInvoked()
        {
            var port = Ports.GetNext();
            using (CreateServer<MockCEchoProvider>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "NOTACCEPTEDSCP");

                var reason = DicomRejectReason.NoReasonGiven;
                client.AssociationRejected += (sender, args) => reason = args.Reason;

                client.AddRequest(new DicomCEchoRequest());
                var exception = await Record.ExceptionAsync(() => client.SendAsync());

                Assert.Equal(DicomRejectReason.CalledAENotRecognized, reason);
                Assert.NotNull(exception);
            }
        }

        [Fact]
        public async Task AssociationReleased_SuccessfulSend_IsInvoked()
        {
            var port = Ports.GetNext();
            using (CreateServer<DicomCEchoProvider>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");

                var released = false;
                var handle = new ManualResetEventSlim();
                client.AssociationReleased += (sender, args) =>
                {
                    released = true;
                    handle.Set();
                };

                client.AddRequest(new DicomCEchoRequest());
                await client.SendAsync();

                handle.Wait(1000);
                Assert.True(released);
            }
        }

        [Fact]
        public async Task Send_RecordAssociationData_AssociationContainsHostAndPort()
        {
            int port = Ports.GetNext();
            using (CreateServer<MockCEchoProvider>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                client.AddRequest(new DicomCEchoRequest());
                await client.SendAsync();

                Assert.NotNull(remoteHost);
                Assert.True(remotePort > 0);
                Assert.NotEqual(port, remotePort);
            }
        }

        [Fact]
        public async Task Send_RejectedAssociation_ShouldYieldException()
        {
            var port = Ports.GetNext();
            using (CreateServer<MockCEchoProvider>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "INVALID");
                client.AddRequest(new DicomCEchoRequest());
                var exception = await Record.ExceptionAsync(() => client.SendAsync());
                Assert.IsType<DicomAssociationRejectedException>(exception);
            }
        }

        [Fact]
        public async Task SendAsync_RejectedAssociation_ShouldYieldException()
        {
            var port = Ports.GetNext();
            using (CreateServer<MockCEchoProvider>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "INVALID");
                client.AddRequest(new DicomCEchoRequest());
                var exception =
                    await
                        Record.ExceptionAsync(() => client.SendAsync())
                            .ConfigureAwait(false);
                Assert.IsType<DicomAssociationRejectedException>(exception);
            }
        }

        [Fact(Skip = "Requires external C-ECHO SCP")]
        public async Task Send_EchoRequestToExternalServer_ShouldSucceed()
        {
            var result = false;
            var awaiter = new ManualResetEventSlim();

            var client = CreateClient("127.0.0.1", 11112, false, "SCU", "ANY-SCP");
            var req = new DicomCEchoRequest();
            req.OnResponseReceived = (rq, rsp) =>
            {
                if (rsp.Status == DicomStatus.Success)
                {
                    result = true;
                }

                awaiter.Set();
            };
            client.AddRequest(req);

            try
            {
                await client.SendAsync();
                awaiter.Wait();
            }
            catch (Exception ex)
            {
                result = false;
                client.Logger.Error("Send failed, exception: {0}", ex);
                awaiter.Set();
            }

            Assert.True(result);
        }

        [Fact]
        public async Task IsSendRequired_AddedRequestNotConnected_ReturnsTrue()
        {
            var port = Ports.GetNext();
            using (CreateServer<DicomCEchoProvider>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                client.AddRequest(new DicomCEchoRequest());
                Assert.True(client.IsSendRequired);
                await client.SendAsync();
                Thread.Sleep(100);

                client.AddRequest(new DicomCEchoRequest());

                Assert.True(client.IsSendRequired);
            }
        }

        [Fact]
        public async Task IsSendRequired_NoRequestNotConnected_ReturnsFalse()
        {
            var port = Ports.GetNext();
            using (CreateServer<DicomCEchoProvider>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                client.AddRequest(new DicomCEchoRequest {OnResponseReceived = (req, res) => Thread.Sleep(100)});
                await client.SendAsync();

                Assert.False(client.IsSendRequired);
            }
        }

        [Fact]
        public void IsSendRequired_AddedRequestIsConnected_ReturnsFalse()
        {
            var port = Ports.GetNext();
            using (CreateServer<DicomCEchoProvider>(port))
            {
                var counter = 0;
                var flag = new ManualResetEventSlim();

                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                client.AssociationLingerTimeoutInMs = 100;
                client.AddRequest(
                    new DicomCEchoRequest {OnResponseReceived = (req, res) => Interlocked.Increment(ref counter)});
                client.SendAsync();

                client.AddRequest(
                    new DicomCEchoRequest
                    {
                        OnResponseReceived = (req, res) =>
                        {
                            Interlocked.Increment(ref counter);
                            flag.Set();
                        }
                    });
                Assert.False(client.IsSendRequired);

                flag.Wait(1000);
                Assert.Equal(2, counter);
            }
        }

        [Fact]
        public async Task Send_ToExplicitOnlyProvider_NotAccepted()
        {
            var port = Ports.GetNext();
            using (CreateServer<ExplicitLECStoreProvider>(port))
            {
                var request = new DicomCStoreRequest(@"./Test Data/CR-MONO1-10-chest");

                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                client.AddRequest(request);

                var exception = await Record.ExceptionAsync(() => client.SendAsync());

                Assert.IsType<DicomAssociationRejectedException>(exception);
            }
        }

        [Theory]
        [InlineData(200)]
        public async Task Send_Plus128CStoreRequestsCompressedTransferSyntax_NoOverflowContextIdsAllRequestsRecognized(int expected)
        {
            var port = Ports.GetNext();
            using (CreateServer<SimpleCStoreProvider>(port))
            {
                var actual = 0;

                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                client.NegotiateAsyncOps(expected, 1);

                for (var i = 0; i < expected; ++i)
                    client.AddRequest(new DicomCStoreRequest(@"./Test Data/CT1_J2KI")
                    {
                        OnResponseReceived = (req, res) => Interlocked.Increment(ref actual)
                    });

                var exception = await Record.ExceptionAsync(() => client.SendAsync());

                Assert.Null(exception);
                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public async Task Cancel_BeforeSend_ShouldNeverConnect()
        {
            var port = Ports.GetNext();
            using (var server = CreateServer<RecordingDicomCEchoProvider, RecordingDicomCEchoProviderServer>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                var cancellationTokenSource = new CancellationTokenSource();
                var numberOfRequestsSent = 5;
                var numberOfResponsesReceived = 0;
                for (var i = 0; i < numberOfRequestsSent; ++i)
                {
                    client.AddRequest(new DicomCEchoRequest
                    {
                        OnResponseReceived = (request, response) => { Interlocked.Increment(ref numberOfResponsesReceived); }
                    });
                }

                bool connected = false;
                client.StateChanged += (sender, args) =>
                {
                    if (args.NewState is DicomClientWithConnectionState)
                    {
                        connected = true;
                    }
                };

                cancellationTokenSource.Cancel();

                await client.SendAsync(cancellationTokenSource.Token);

                cancellationTokenSource.Dispose();

                Assert.Equal(0, numberOfResponsesReceived);
                Assert.Empty(server.Providers);
                Assert.False(connected);
            }
        }

        [Fact]
        public async Task Cancel_AfterConnect_BeforeAssociation_ShouldNeverAssociate()
        {
            var port = Ports.GetNext();
            using (var server = CreateServer<RecordingDicomCEchoProvider, RecordingDicomCEchoProviderServer>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                var cancellationTokenSource = new CancellationTokenSource();
                var numberOfRequestsSent = 5;
                var numberOfResponsesReceived = 0;
                for (var i = 0; i < numberOfRequestsSent; ++i)
                {
                    client.AddRequest(new DicomCEchoRequest
                    {
                        OnResponseReceived = (request, response) => { Interlocked.Increment(ref numberOfResponsesReceived); }
                    });
                }

                bool connected = false, associated = false;
                client.StateChanged += (sender, args) =>
                {
                    if (args.NewState is DicomClientWithConnectionState)
                    {
                        connected = true;
                        cancellationTokenSource.Cancel();
                    }

                    if (args.NewState is DicomClientWithAssociationState)
                    {
                        associated = true;
                    }
                };

                await client.SendAsync(cancellationTokenSource.Token);

                cancellationTokenSource.Dispose();

                Assert.True(connected);
                Assert.False(associated);
                Assert.Empty(server.Providers.SelectMany(p => p.Associations));
                Assert.Empty(server.Providers.SelectMany(p => p.Requests));
                Assert.Equal(0, numberOfResponsesReceived);
            }
        }

        [Fact]
        public async Task Cancel_AfterAssociation_BeforeSend_ShouldNeverSend()
        {
            var port = Ports.GetNext();
            using (var server = CreateServer<RecordingDicomCEchoProvider, RecordingDicomCEchoProviderServer>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                var cancellationTokenSource = new CancellationTokenSource();
                var numberOfRequestsSent = 5;
                var numberOfResponsesReceived = 0;
                for (var i = 0; i < numberOfRequestsSent; ++i)
                {
                    client.AddRequest(new DicomCEchoRequest
                    {
                        OnResponseReceived = (request, response) => { Interlocked.Increment(ref numberOfResponsesReceived); }
                    });
                }

                bool connected = false, associated = false;
                client.StateChanged += (sender, args) =>
                {
                    if (args.NewState is DicomClientWithConnectionState)
                    {
                        connected = true;
                    }

                    if (args.NewState is DicomClientWithAssociationState)
                    {
                        associated = true;
                        cancellationTokenSource.Cancel();
                    }
                };

                await client.SendAsync(cancellationTokenSource.Token);

                cancellationTokenSource.Dispose();

                Assert.True(connected);
                Assert.True(associated);
                Assert.NotEmpty(server.Providers.SelectMany(p => p.Associations));
                Assert.Empty(server.Providers.SelectMany(p => p.Requests));
                Assert.Equal(0, numberOfResponsesReceived);
            }
        }

        [Fact]
        public async Task Cancel_DuringSend_ShouldStopSending()
        {
            var port = Ports.GetNext();
            using (var server = CreateServer<RecordingDicomCEchoProvider, RecordingDicomCEchoProviderServer>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                var cancellationTokenSource = new CancellationTokenSource();

                var sentRequests = new ConcurrentBag<DicomCEchoRequest>();
                server.OnCEchoRequest(request =>
                {
                    sentRequests.Add(request);
                    if (sentRequests.Count > 0)
                    {
                        cancellationTokenSource.Cancel();
                    }
                });

                var numberOfRequestsSent = 5;
                var numberOfResponsesReceived = 0;
                client.NegotiateAsyncOps(1, 1);
                for (var i = 0; i < numberOfRequestsSent; ++i)
                {
                    client.AddRequest(new DicomCEchoRequest
                    {
                        OnResponseReceived = (request, response) =>
                        {
                            Interlocked.Increment(ref numberOfResponsesReceived);
                        }
                    });
                }

                bool connected = false, associated = false;
                client.StateChanged += (sender, args) =>
                {
                    if (args.NewState is DicomClientWithConnectionState)
                    {
                        connected = true;
                    }

                    if (args.NewState is DicomClientWithAssociationState)
                    {
                        associated = true;
                    }
                };

                await client.SendAsync(cancellationTokenSource.Token);

                cancellationTokenSource.Dispose();

                Assert.True(connected);
                Assert.True(associated);
                Assert.NotEmpty(server.Providers.SelectMany(p => p.Associations));
                Assert.NotEmpty(server.Providers.SelectMany(p => p.Requests));
                Assert.Equal(1, numberOfResponsesReceived);
            }
        }

        [Fact]
        public async Task Cancel_DuringAssociationRelease_ShouldNotLinger()
        {
            var port = Ports.GetNext();
            using (var server = CreateServer<RecordingDicomCEchoProvider, RecordingDicomCEchoProviderServer>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                var cancellationTokenSource = new CancellationTokenSource();
                var numberOfRequestsSent = 5;
                var numberOfResponsesReceived = 0;
                client.NegotiateAsyncOps(1, 1);
                for (var i = 0; i < numberOfRequestsSent; ++i)
                {
                    var i1 = i;
                    client.AddRequest(new DicomCEchoRequest
                    {
                        OnResponseReceived = (request, response) => { Interlocked.Increment(ref numberOfResponsesReceived); }
                    });
                }

                bool connected = false, associated = false;
                client.StateChanged += (sender, args) =>
                {
                    if (args.NewState is DicomClientWithConnectionState)
                    {
                        connected = true;
                    }

                    if (args.NewState is DicomClientWithAssociationState)
                    {
                        associated = true;
                    }

                    if (args.NewState is DicomClientReleaseAssociationState)
                    {
                        cancellationTokenSource.Cancel();
                    }
                };

                await client.SendAsync(cancellationTokenSource.Token);

                cancellationTokenSource.Dispose();

                Assert.True(connected);
                Assert.True(associated);
                Assert.NotEmpty(server.Providers.SelectMany(p => p.Associations));
                Assert.NotEmpty(server.Providers.SelectMany(p => p.Requests));
                Assert.Equal(5, numberOfResponsesReceived);
            }
        }

        private Task<DicomCEchoResponse> SendEchoRequestWithTimeout(DicomClient dicomClient, int timeoutInMilliseconds = 3000)
        {
            var request = new DicomCEchoRequest();
            var logger = _logger.IncludePrefix("C-Echo request");

            var responseCompletionSource = new TaskCompletionSource<DicomCEchoResponse>();
            var responseCancellationSource = new CancellationTokenSource(timeoutInMilliseconds);

            var cancellationRegistration = responseCancellationSource.Token.Register(() =>
            {
                logger.Error($"Request [{request.MessageID}] timed out!");
                responseCompletionSource.SetCanceled();
            });

            request.OnResponseReceived += (req, res) =>
            {
                logger.Info($"Response [{request.MessageID}] received!");
                responseCompletionSource.SetResult(res);
            };

            dicomClient.AddRequest(request);

            // ReSharper disable once MethodSupportsCancellation Let's not cancel the cancellation, ha ha!
            responseCompletionSource.Task.ContinueWith(_ => cancellationRegistration.Dispose());

            return responseCompletionSource.Task;
        }

        private void AllResponsesShouldHaveSucceeded(IEnumerable<DicomCEchoResponse> responses)
        {
            var logger = _logger.IncludePrefix("Responses");
            foreach (var r in responses)
            {
                logger.Info($"{r.Type} [{r.RequestMessageID}]: " +
                            $"Status = {r.Status.State}, " +
                            $"Code = {r.Status.Code}, " +
                            $"ErrorComment = {r.Status.ErrorComment}, " +
                            $"Description = {r.Status.Description}");

                Assert.Equal(DicomState.Success, r.Status.State);
            }
        }

        [Theory]
        [InlineData( /*number of requests:*/ 6, /* seconds between each request: */ 1, /* linger: */ 5)]
        [InlineData( /*number of requests:*/ 3, /* seconds between each request: */ 2, /* linger: */ 5)]
        public async Task SendAsync_Linger_ShouldLingerLongEnoughToReuseAssociation(int numberOfRequests, int secondsBetweenEachRequest, int lingerTimeoutInSeconds)
        {
            var logger = _logger.IncludePrefix("UnitTest");
            var port = Ports.GetNext();
            var expectedNumberOfAssociations = 1;

            using (var server = CreateServer<RecordingDicomCEchoProvider, RecordingDicomCEchoProviderServer>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");

                client.AssociationLingerTimeoutInMs = lingerTimeoutInSeconds * 1000;

                logger.Info($"Beginning {numberOfRequests} parallel requests with {secondsBetweenEachRequest}s between each request");

                var requests = new List<Task<DicomCEchoResponse>>();

                var sendTasks = new List<Task>();

                for (var i = 1; i <= numberOfRequests; i++)
                {
                    var task = SendEchoRequestWithTimeout(client);
                    requests.Add(task);

                    if (client.IsSendRequired)
                    {
                        // Do not await here, because this task will only complete after the client has completely processed the request
                        sendTasks.Add(client.SendAsync());
                    }

                    if (i < numberOfRequests)
                    {
                        logger.Info($"Waiting {secondsBetweenEachRequest} seconds between requests");
                        await Task.Delay(TimeSpan.FromSeconds(secondsBetweenEachRequest)).ConfigureAwait(false);
                        logger.Info($"Waited {secondsBetweenEachRequest} seconds, moving on to next request");
                    }
                }

                var responses = await Task.WhenAll(requests).ConfigureAwait(false);

                AllResponsesShouldHaveSucceeded(responses);

                Assert.Equal(numberOfRequests, responses.Length);

                var associations = server.Providers.SelectMany(p => p.Associations).ToList();

                Assert.Equal(expectedNumberOfAssociations, associations.Count);

                var receivedRequests = server.Providers.SelectMany(p => p.Requests).ToList();

                Assert.Equal(numberOfRequests, receivedRequests.Count);

                // now let the DicomClient complete gracefully
                await Task.WhenAll(sendTasks).ConfigureAwait(false);
            }
        }

        [Theory]
        [InlineData( /*number of requests:*/ 6, /* seconds between each request: */ 1, /* linger: */ 5)]
        [InlineData( /*number of requests:*/ 2, /* seconds between each request: */ 4, /* linger: */ 5)]
        public async Task SendAsync_Linger_ShouldKeepDelayingLingerAsLongAsRequestsAreComingIn(int numberOfRequests, int secondsBetweenEachRequest, int lingerTimeoutInSeconds)
        {
            var logger = _logger.IncludePrefix("UnitTest");
            var port = Ports.GetNext();
            var expectedNumberOfAssociations = 1;
            using (var server = CreateServer<RecordingDicomCEchoProvider, RecordingDicomCEchoProviderServer>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");

                client.AssociationLingerTimeoutInMs = lingerTimeoutInSeconds * 1000;

                logger.Info($"Beginning {numberOfRequests} parallel requests with {secondsBetweenEachRequest}s between each request");

                var requests = new List<Task<DicomCEchoResponse>>();

                var sendTasks = new List<Task>();

                for (var i = 1; i <= numberOfRequests; i++)
                {
                    var task = SendEchoRequestWithTimeout(client);
                    requests.Add(task);

                    if (client.IsSendRequired)
                    {
                        // Do not await here, because this task will only complete after the client has completely processed the request
                        sendTasks.Add(client.SendAsync());
                    }

                    if (i < numberOfRequests)
                    {
                        logger.Info($"Waiting {secondsBetweenEachRequest} seconds between requests");
                        await Task.Delay(TimeSpan.FromSeconds(secondsBetweenEachRequest)).ConfigureAwait(false);
                        logger.Info($"Waited {secondsBetweenEachRequest} seconds, moving on to next request");
                    }
                }

                var responses = await Task.WhenAll(requests).ConfigureAwait(false);

                AllResponsesShouldHaveSucceeded(responses);

                Assert.Equal(numberOfRequests, responses.Length);

                var associations = server.Providers.SelectMany(p => p.Associations).ToList();

                Assert.Equal(expectedNumberOfAssociations, associations.Count);

                var receivedRequests = server.Providers.SelectMany(p => p.Requests).ToList();

                Assert.Equal(numberOfRequests, receivedRequests.Count);

                // now let the DicomClient complete gracefully
                await Task.WhenAll(sendTasks).ConfigureAwait(false);
            }
        }

        [Theory]
        [InlineData( /*number of requests:*/ 2, /* seconds between each request: */ 2, /* linger: */ 1)]
        [InlineData( /*number of requests:*/ 2, /* seconds between each request: */ 3, /* linger: */ 2)]
        public async Task SendAsync_Linger_ShouldAutomaticallyOpenNewAssociationAfterLingerTime(int numberOfRequests, int secondsBetweenEachRequest, int lingerTimeoutInSeconds)
        {
            var logger = _logger.IncludePrefix("UnitTest");
            var port = Ports.GetNext();
            // Each request should have its own association
            var expectedNumberOfAssociations = 2;
            using (var server = CreateServer<RecordingDicomCEchoProvider, RecordingDicomCEchoProviderServer>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");

                client.AssociationLingerTimeoutInMs = lingerTimeoutInSeconds * 1000;

                logger.Info($"Beginning {numberOfRequests} parallel requests with {secondsBetweenEachRequest}s between each request");

                var requests = new List<Task<DicomCEchoResponse>>();

                var sendTasks = new List<Task>();

                for (var i = 1; i <= numberOfRequests; i++)
                {
                    var task = SendEchoRequestWithTimeout(client);
                    requests.Add(task);

                    if (client.IsSendRequired)
                    {
                        // Do not await here, because this task will only complete after the client has completely processed the request
                        sendTasks.Add(client.SendAsync());
                    }

                    if (i < numberOfRequests)
                    {
                        logger.Info($"Waiting {secondsBetweenEachRequest} seconds between requests");
                        await Task.Delay(TimeSpan.FromSeconds(secondsBetweenEachRequest)).ConfigureAwait(false);
                        logger.Info($"Waited {secondsBetweenEachRequest} seconds, moving on to next request");
                    }
                }

                var responses = await Task.WhenAll(requests).ConfigureAwait(false);

                AllResponsesShouldHaveSucceeded(responses);

                Assert.Equal(numberOfRequests, responses.Length);

                var associations = server.Providers.SelectMany(p => p.Associations).ToList();

                Assert.Equal(expectedNumberOfAssociations, associations.Count);

                var receivedRequests = server.Providers.SelectMany(p => p.Requests).ToList();

                Assert.Equal(numberOfRequests, receivedRequests.Count);

                // now let the DicomClient complete gracefully
                await Task.WhenAll(sendTasks).ConfigureAwait(false);
            }
        }

        [Fact]
        public async Task SendAsync_Linger_ShouldAutomaticallyOpenNewAssociationAfterLingerTimeAfterLastRequest()
        {
            var numberOfRequests = 5;
            var lingerTimeoutInSeconds = 5;
            var secondsBetweenEachRequest = new[] {1, 1, 1, 6, 1};
            var expectedNumberOfAssociations = 2;
            var logger = _logger.IncludePrefix("UnitTest");
            var port = Ports.GetNext();

            using (var server = CreateServer<RecordingDicomCEchoProvider, RecordingDicomCEchoProviderServer>(port))
            {
                var client = CreateClient("127.0.0.1", port, false, "SCU", "ANY-SCP");

                client.AssociationLingerTimeoutInMs = lingerTimeoutInSeconds * 1000;

                logger.Info($"Beginning {numberOfRequests} parallel requests with variable wait times between each request");

                var requests = new List<Task<DicomCEchoResponse>>();

                var sendTasks = new List<Task>();

                for (var i = 1; i <= numberOfRequests; i++)
                {
                    var task = SendEchoRequestWithTimeout(client, 10000);
                    requests.Add(task);

                    if (client.IsSendRequired)
                    {
                        // Do not await here, because this task will only complete after the client has completely processed the request
                        sendTasks.Add(client.SendAsync());
                    }

                    if (i < numberOfRequests)
                    {
                        var secondsToWait = secondsBetweenEachRequest[i];
                        logger.Info($"Waiting {secondsBetweenEachRequest} seconds between requests");
                        await Task.Delay(TimeSpan.FromSeconds(secondsToWait)).ConfigureAwait(false);
                        logger.Info($"Waited {secondsBetweenEachRequest} seconds, moving on to next request");
                    }

                }

                var responses = await Task.WhenAll(requests).ConfigureAwait(false);

                AllResponsesShouldHaveSucceeded(responses);

                Assert.Equal(numberOfRequests, responses.Length);

                var associations = server.Providers.SelectMany(p => p.Associations).ToList();

                Assert.Equal(expectedNumberOfAssociations, associations.Count);

                var receivedRequests = server.Providers.SelectMany(p => p.Requests).ToList();

                Assert.Equal(numberOfRequests, receivedRequests.Count);

                // now let the DicomClient complete gracefully
                await Task.WhenAll(sendTasks).ConfigureAwait(false);
            }
        }

        #region Support classes

        public class MockCEchoProvider : DicomService, IDicomServiceProvider, IDicomCEchoProvider
        {
            public MockCEchoProvider(INetworkStream stream, Encoding fallbackEncoding, Logger log)
                : base(stream, fallbackEncoding, log)
            {
            }

            public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
            {
                foreach (var pc in association.PresentationContexts)
                {
                    pc.AcceptTransferSyntaxes(DicomTransferSyntax.ImplicitVRLittleEndian);
                }

                if (association.CalledAE.Equals("ANY-SCP", StringComparison.OrdinalIgnoreCase))
                {
                    Thread.Sleep(1000);
                    remoteHost = association.RemoteHost;
                    remotePort = association.RemotePort;
                    return SendAssociationAcceptAsync(association);
                }

                return SendAssociationRejectAsync(DicomRejectResult.Permanent, DicomRejectSource.ServiceUser,
                    DicomRejectReason.CalledAENotRecognized);
            }

            public Task OnReceiveAssociationReleaseRequestAsync()
            {
                return SendAssociationReleaseResponseAsync();
            }

            public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
            {
            }

            public void OnConnectionClosed(Exception exception)
            {
            }

            public DicomCEchoResponse OnCEchoRequest(DicomCEchoRequest request)
            {
                return new DicomCEchoResponse(request, DicomStatus.Success);
            }
        }

        /// <summary>
        /// Artificial C-STORE provider, only supporting Explicit LE transfer syntax for the purpose of
        /// testing <see cref="Send_ToExplicitOnlyProvider_NotAccepted"/>.
        /// </summary>
        private class ExplicitLECStoreProvider : DicomService, IDicomServiceProvider, IDicomCStoreProvider
        {
            private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes =
            {
                DicomTransferSyntax.ExplicitVRLittleEndian
            };

            public ExplicitLECStoreProvider(INetworkStream stream, Encoding fallbackEncoding, Logger log)
                : base(stream, fallbackEncoding, log)
            {
            }

            public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
            {
                foreach (var pc in association.PresentationContexts)
                {
                    if (!pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes))
                    {
                        return SendAssociationRejectAsync(DicomRejectResult.Permanent,
                            DicomRejectSource.ServiceProviderACSE, DicomRejectReason.ApplicationContextNotSupported);
                    }
                }

                return SendAssociationAcceptAsync(association);
            }

            public Task OnReceiveAssociationReleaseRequestAsync()
            {
                return SendAssociationReleaseResponseAsync();
            }

            public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
            {
            }

            public void OnConnectionClosed(Exception exception)
            {
            }

            public DicomCStoreResponse OnCStoreRequest(DicomCStoreRequest request)
            {
                return new DicomCStoreResponse(request, DicomStatus.Success);
            }

            public void OnCStoreRequestException(string tempFileName, Exception e)
            {
            }
        }

        public class RecordingDicomCEchoProvider : DicomService, IDicomServiceProvider, IDicomCEchoProvider
        {
            private readonly ConcurrentBag<DicomAssociation> _associations;
            private readonly ConcurrentBag<DicomCEchoRequest> _requests;
            private readonly Action<DicomCEchoRequest> _onRequest;

            public IEnumerable<DicomCEchoRequest> Requests => _requests;
            public IEnumerable<DicomAssociation> Associations => _associations;

            public RecordingDicomCEchoProvider(INetworkStream stream, Encoding fallbackEncoding, Logger log, Action<DicomCEchoRequest> onRequest)
                : base(stream, fallbackEncoding, log)
            {
                _onRequest = onRequest ?? throw new ArgumentNullException(nameof(onRequest));
                _requests = new ConcurrentBag<DicomCEchoRequest>();
                _associations = new ConcurrentBag<DicomAssociation>();
            }

            async Task WaitForALittleBit()
            {
                var ms = new Random().Next(10);
                await Task.Delay(ms);
            }

            /// <inheritdoc />
            public async Task OnReceiveAssociationRequestAsync(DicomAssociation association)
            {
                await WaitForALittleBit();
                foreach (var pc in association.PresentationContexts)
                {
                    pc.SetResult(DicomPresentationContextResult.Accept);
                }

                _associations.Add(association);

                await SendAssociationAcceptAsync(association);
            }

            /// <inheritdoc />
            public async Task OnReceiveAssociationReleaseRequestAsync()
            {
                await WaitForALittleBit();
                await SendAssociationReleaseResponseAsync();
            }

            /// <inheritdoc />
            public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
            {
            }

            /// <inheritdoc />
            public void OnConnectionClosed(Exception exception)
            {
            }

            public DicomCEchoResponse OnCEchoRequest(DicomCEchoRequest request)
            {
                _onRequest(request);

                _requests.Add(request);

                WaitForALittleBit().GetAwaiter().GetResult();

                return new DicomCEchoResponse(request, DicomStatus.Success);
            }
        }

        public class RecordingDicomCEchoProviderServer : DicomServer<RecordingDicomCEchoProvider>
        {
            private readonly ConcurrentBag<RecordingDicomCEchoProvider> _providers;
            private Action<DicomCEchoRequest> _onRequest;

            public IEnumerable<RecordingDicomCEchoProvider> Providers => _providers;

            public RecordingDicomCEchoProviderServer()
            {
                _providers = new ConcurrentBag<RecordingDicomCEchoProvider>();
                _onRequest = _ => { };
            }

            public void OnCEchoRequest(Action<DicomCEchoRequest> onRequest)
            {
                _onRequest = onRequest;
            }

            protected sealed override RecordingDicomCEchoProvider CreateScp(INetworkStream stream)
            {
                var provider = new RecordingDicomCEchoProvider(stream, Encoding.UTF8, Logger, _onRequest);
                _providers.Add(provider);
                return provider;
            }
        }

        #endregion
    }
}
