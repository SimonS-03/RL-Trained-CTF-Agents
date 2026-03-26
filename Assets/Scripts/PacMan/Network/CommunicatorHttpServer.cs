using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;
using Google.Protobuf;
using PacMan.Interface.PacMan;
using PacMan.Network.Generated;
using UnityEngine;
using ProtoGameState = PacMan.Network.Generated.GameState;

namespace PacMan.Network
{
    public class CommunicatorHttpServer
    {
        public static int channel = 50000;
        private static readonly Lazy<CommunicatorHttpServer> _sLazy = new(() => new CommunicatorHttpServer());

        public static CommunicatorHttpServer Instance => _sLazy.Value;
        public static bool IsInitialized => _sLazy.IsValueCreated;

        public IReadOnlyList<string> ClientIdentifiers
        {
            get
            {
                lock (_messageLock)
                {
                    return new List<string>(_clientOrder);
                }
            }
        }

        private readonly object _messageLock = new();
        private readonly Dictionary<string, ClientSession> _clientsById = new();
        private readonly List<string> _clientOrder = new();
        private readonly Dictionary<string, PendingStepRequest> _pendingStepRequests = new();

        private Dictionary<string, PendingStepRequest> _activeStepRequests = new();
        private List<ProtoGameState> _latestGameStates = new();
        private TcpListener _tcpListener;
        private Thread _listenerThread;
        private bool _isRunning = true;
        private int _expectedClientCount = TeamAssignmentUtil.ExpectedNetworkClientCount;
        private int _startedWaitCycleCount;
        private bool _trackCurrentWaitCycle;
        private long _currentWaitCycleStartTimestamp = -1;

        private CommunicatorHttpServer()
        {
            _tcpListener = ListenerSetup();

            _listenerThread = new Thread(StartListener)
            {
                IsBackground = true
            };
            _listenerThread.Start();

            Debug.Log($"Communication Server started at http://127.0.0.1:{channel}/");
        }

        public IReadOnlyList<NetworkRequestDisplayEntry> GetRequestDisplayEntries()
        {
            lock (_messageLock)
            {
                var nowTimestamp = Stopwatch.GetTimestamp();
                var entries = new List<NetworkRequestDisplayEntry>(_clientOrder.Count);
                foreach (var clientId in _clientOrder)
                {
                    if (!_clientsById.TryGetValue(clientId, out var client))
                    {
                        continue;
                    }

                    var teamTag = GetClientTeamTag_NoLock(client.Index);
                    var currentWaitMs = client.CurrentCycleWaitMs;
                    if (_trackCurrentWaitCycle &&
                        !client.HasSubmittedRequestThisCycle &&
                        _currentWaitCycleStartTimestamp >= 0)
                    {
                        currentWaitMs = GetElapsedMilliseconds(_currentWaitCycleStartTimestamp, nowTimestamp);
                    }

                    var averageWaitMs = client.RecordedRequestCount > 0
                        ? client.TotalRequestWaitMs / client.RecordedRequestCount
                        : 0d;

                    entries.Add(new NetworkRequestDisplayEntry(
                        teamTag,
                        ResolveClientDisplayName_NoLock(client, teamTag),
                        averageWaitMs,
                        currentWaitMs));
                }

                return entries;
            }
        }

        public void ResetRequestMetrics()
        {
            lock (_messageLock)
            {
                _startedWaitCycleCount = 0;
                _trackCurrentWaitCycle = false;
                _currentWaitCycleStartTimestamp = -1;
                foreach (var client in _clientsById.Values)
                {
                    client.ResetRequestMetrics();
                }
            }
        }

        public void StartStepRequestWaitCycle()
        {
            lock (_messageLock)
            {
                _startedWaitCycleCount += 1;
                _trackCurrentWaitCycle = _startedWaitCycleCount > 1;
                _currentWaitCycleStartTimestamp = _trackCurrentWaitCycle ? Stopwatch.GetTimestamp() : -1;

                foreach (var clientId in _clientOrder)
                {
                    if (_clientsById.TryGetValue(clientId, out var client))
                    {
                        client.BeginWaitCycle();
                    }
                }
            }
        }

        public List<List<PacManAction>> FetchActionPerClient()
        {
            lock (_messageLock)
            {
                while (_isRunning && !HasAllClientActions_NoLock())
                {
                    Monitor.Wait(_messageLock);
                }

                if (!_isRunning)
                {
                    return new List<List<PacManAction>>();
                }

                var actionsByClient = new List<List<PacManAction>>(_expectedClientCount);
                MovePendingActionsToActive_NoLock(actionsByClient);
                return actionsByClient;
            }
        }

        public bool TryFetchActionPerClient(out List<List<PacManAction>> actionsByClient)
        {
            lock (_messageLock)
            {
                if (!HasAllClientActions_NoLock())
                {
                    actionsByClient = null;
                    return false;
                }

                actionsByClient = new List<List<PacManAction>>(_expectedClientCount);
                MovePendingActionsToActive_NoLock(actionsByClient);
                return true;
            }
        }

        public void SetLatestStates(List<ProtoGameState> gameStatePerClient)
        {
            lock (_messageLock)
            {
                SetLatestStates_NoLock(gameStatePerClient);
                Monitor.PulseAll(_messageLock);
            }
        }

        public void StepCompleted(List<ProtoGameState> gameStatePerClient)
        {
            List<(TaskCompletionSource<ProtoGameState> responseSource, ProtoGameState state)> completions = null;

            lock (_messageLock)
            {
                SetLatestStates_NoLock(gameStatePerClient);
                Monitor.PulseAll(_messageLock);

                if (_activeStepRequests.Count == 0)
                {
                    return;
                }

                completions = new List<(TaskCompletionSource<ProtoGameState> responseSource, ProtoGameState state)>(_activeStepRequests.Count);
                for (var i = 0; i < _clientOrder.Count; i++)
                {
                    var clientId = _clientOrder[i];
                    if (!_activeStepRequests.TryGetValue(clientId, out var pendingStep))
                    {
                        continue;
                    }

                    var state = BuildStateForClient_NoLock(i, clientId);
                    completions.Add((pendingStep.ResponseSource, state));
                }

                _activeStepRequests = new Dictionary<string, PendingStepRequest>();
            }

            foreach (var completion in completions)
            {
                completion.responseSource.TrySetResult(completion.state);
            }
        }

        private static TcpListener ListenerSetup()
        {
            while (true)
            {
                var listener = new TcpListener(IPAddress.Any, channel);
                try
                {
                    listener.Start();
                    return listener;
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    listener.Stop();
                    channel += 1;
                }
                catch (Exception e)
                {
                    listener.Stop();
                    throw new InvalidOperationException($"Failed to start server on port {channel}.", e);
                }
            }
        }

        private void StartListener()
        {
            while (_isRunning)
            {
                TcpClient client = null;
                try
                {
                    client = _tcpListener.AcceptTcpClient();
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch (SocketException) when (!_isRunning)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (ThreadAbortException)
                {
                    Thread.ResetAbort();
                    break;
                }
                catch (Exception e)
                {
                    Debug.LogError(e);
                    try
                    {
                        client?.Close();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream);
                    if (request == null)
                    {
                        return;
                    }

                    var response = await HandleRequestAsync(request);
                    await WriteResponseAsync(stream, response);
                }
                catch (ThreadAbortException)
                {
                    Thread.ResetAbort();
                }
                catch (Exception e)
                {
                    Debug.LogError(e);
                }
            }
        }

        private async Task<HttpResponseData> HandleRequestAsync(HttpRequestData request)
        {
            if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return CreateErrorResponse(405, "Only POST is supported");
            }

            if (request.Path.Contains("/initialize", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleInitializeAsync(request);
            }

            if (request.Path.Contains("/step", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleStepAsync(request);
            }

            return CreateErrorResponse(404, "Unknown route");
        }

        private Task<HttpResponseData> HandleInitializeAsync(HttpRequestData request)
        {
            var incoming = request.Body != null && request.Body.Length > 0
                ? InitializeRequest.Parser.ParseFrom(request.Body)
                : new InitializeRequest();
            var requestedTeamName = GetRequestedTeamName(request.Headers);

            ProtoGameState responseState = null;
            ClientSession responseClient = null;
            int? status = null;
            string error = null;

            lock (_messageLock)
            {
                if (!string.IsNullOrWhiteSpace(incoming.ClientId))
                {
                    if (_clientsById.TryGetValue(incoming.ClientId, out var existingClient))
                    {
                        responseClient = existingClient;
                        UpdateClientTeamName_NoLock(responseClient, requestedTeamName);
                    }
                    else
                    {
                        status = 404;
                        error = "Unknown client id";
                    }
                }
                else if (_clientOrder.Count >= _expectedClientCount)
                {
                    status = 409;
                    error = "All client slots are already assigned";
                }
                else
                {
                    var clientId = Guid.NewGuid().ToString("N");
                    var client = new ClientSession(clientId, _clientOrder.Count);
                    UpdateClientTeamName_NoLock(client, requestedTeamName);
                    _clientsById[clientId] = client;
                    _clientOrder.Add(clientId);
                    responseClient = client;
                    Debug.Log($"Client connected with id {client.ClientId} ({_clientOrder.Count}/{_expectedClientCount})");
                    Monitor.PulseAll(_messageLock);
                }

                while (!status.HasValue && _isRunning && _latestGameStates.Count == 0)
                {
                    Monitor.Wait(_messageLock);
                }

                if (!status.HasValue)
                {
                    if (!_isRunning)
                    {
                        status = 503;
                        error = "Server stopped";
                    }
                    else if (responseClient != null)
                    {
                        responseState = BuildStateForClient_NoLock(responseClient.Index, responseClient.ClientId);
                        if (string.IsNullOrWhiteSpace(incoming.ClientId))
                        {
                            Debug.Log($"Client {responseClient.ClientId} assigned to {DescribeClientOwnership_NoLock(responseClient.Index)}.");
                        }
                    }
                }
            }

            if (status.HasValue)
            {
                return Task.FromResult(CreateErrorResponse(status.Value, error));
            }

            return Task.FromResult(CreateBinaryResponse(200, responseState.ToByteArray()));
        }

        private async Task<HttpResponseData> HandleStepAsync(HttpRequestData request)
        {
            StepRequest incoming;
            try
            {
                incoming = StepRequest.Parser.ParseFrom(request.Body ?? Array.Empty<byte>());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Invalid step payload: {e.Message}");
                return CreateErrorResponse(400, "Invalid step payload");
            }

            var requestedTeamName = GetRequestedTeamName(request.Headers);

            if (incoming == null || string.IsNullOrWhiteSpace(incoming.ClientId))
            {
                return CreateErrorResponse(400, "Missing client id");
            }

            Task<ProtoGameState> responseTask = null;
            int? status = null;
            string error = null;

            lock (_messageLock)
            {
                if (!_clientsById.TryGetValue(incoming.ClientId, out var client))
                {
                    status = 404;
                    error = "Unknown client id";
                }
                else if (_pendingStepRequests.ContainsKey(incoming.ClientId) || _activeStepRequests.ContainsKey(incoming.ClientId))
                {
                    status = 409;
                    error = "Client already has an outstanding step request";
                }
                else
                {
                    UpdateClientTeamName_NoLock(client, requestedTeamName);
                    RecordWaitForRequest_NoLock(client);
                    var responseSource = new TaskCompletionSource<ProtoGameState>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingStepRequests[incoming.ClientId] = new PendingStepRequest(ToLocalActions(incoming.Actions), responseSource);
                    responseTask = responseSource.Task;
                    Monitor.PulseAll(_messageLock);
                }
            }

            if (status.HasValue)
            {
                return CreateErrorResponse(status.Value, error);
            }

            try
            {
                var responseState = await responseTask;
                return CreateBinaryResponse(200, responseState.ToByteArray());
            }
            catch (TaskCanceledException)
            {
                return CreateErrorResponse(503, "Server stopped");
            }
        }

        private static List<PacManAction> ToLocalActions(IEnumerable<ProtoPacManAction> actions)
        {
            var localActions = new List<PacManAction>();
            if (actions == null)
            {
                return localActions;
            }

            foreach (var action in actions)
            {
                localActions.Add(new PacManAction
                {
                    Acceleration = action?.Acceleration == null ? Vector2.zero : new Vector2(action.Acceleration.X, action.Acceleration.Y),
                    Index = action?.Index ?? 0
                });
            }

            return localActions;
        }

        private bool HasAllClientActions_NoLock()
        {
            return _expectedClientCount > 0 &&
                   _clientOrder.Count == _expectedClientCount &&
                   _pendingStepRequests.Count == _expectedClientCount;
        }

        private void MovePendingActionsToActive_NoLock(List<List<PacManAction>> actionsByClient)
        {
            var activeStepRequests = new Dictionary<string, PendingStepRequest>(_expectedClientCount);
            foreach (var clientId in _clientOrder)
            {
                var pendingStep = _pendingStepRequests[clientId];
                actionsByClient.Add(new List<PacManAction>(pendingStep.Actions));
                activeStepRequests[clientId] = pendingStep;
            }

            _pendingStepRequests.Clear();
            _activeStepRequests = activeStepRequests;
        }

        private void SetLatestStates_NoLock(List<ProtoGameState> gameStatePerClient)
        {
            if (gameStatePerClient == null || gameStatePerClient.Count == 0)
            {
                return;
            }

            _latestGameStates = gameStatePerClient.Select(state => state.Clone()).ToList();
            _expectedClientCount = gameStatePerClient.Count;
        }

        private ProtoGameState BuildStateForClient_NoLock(int clientIndex, string clientId)
        {
            var controlledAgentIndex = TeamAssignmentUtil.GetControlledAgentAnchorForClientSlot(GetLatestStateTags_NoLock(), clientIndex, _expectedClientCount);
            if (_latestGameStates.Count == 0)
            {
                return GameStateParser.WithClientContext(new ProtoGameState(), clientId, controlledAgentIndex);
            }

            var safeIndex = Math.Min(clientIndex, _latestGameStates.Count - 1);
            return GameStateParser.WithClientContext(_latestGameStates[safeIndex], clientId, controlledAgentIndex);
        }

        private List<string> GetLatestStateTags_NoLock()
        {
            if (_latestGameStates.Count == 0)
            {
                return new List<string>();
            }

            var latestState = _latestGameStates[0];
            var agentTags = new List<string>(latestState.Agents.Count);
            foreach (var agent in latestState.Agents)
            {
                agentTags.Add(agent?.Tag ?? string.Empty);
            }

            return agentTags;
        }

        private string GetClientTeamTag_NoLock(int clientIndex)
        {
            var agentTags = GetLatestStateTags_NoLock();
            var controlledAgentIndices = TeamAssignmentUtil.GetControlledAgentIndicesForClientSlot(agentTags, clientIndex, _expectedClientCount);
            if (controlledAgentIndices.Count == 0)
            {
                return string.Empty;
            }

            var teamIndex = controlledAgentIndices[0];
            return teamIndex >= 0 && teamIndex < agentTags.Count
                ? agentTags[teamIndex]
                : string.Empty;
        }

        private string DescribeClientOwnership_NoLock(int clientIndex)
        {
            var agentTags = GetLatestStateTags_NoLock();
            var controlledAgentIndices = TeamAssignmentUtil.GetControlledAgentIndicesForClientSlot(agentTags, clientIndex, _expectedClientCount);
            var teamTag = controlledAgentIndices.Count > 0 && controlledAgentIndices[0] < agentTags.Count
                ? agentTags[controlledAgentIndices[0]]
                : "unassigned";
            return $"{teamTag} agents [{string.Join(",", controlledAgentIndices)}]";
        }

        private static string GetRequestedTeamName(IReadOnlyDictionary<string, string> headers)
        {
            if (headers == null || !headers.TryGetValue(CommunicatorHttpClient.TeamNameHeaderName, out var requestedTeamName))
            {
                return string.Empty;
            }

            return requestedTeamName?.Trim() ?? string.Empty;
        }

        private static void UpdateClientTeamName_NoLock(ClientSession client, string requestedTeamName)
        {
            if (client == null || string.IsNullOrWhiteSpace(requestedTeamName))
            {
                return;
            }

            client.TeamName = requestedTeamName;
        }

        private void RecordWaitForRequest_NoLock(ClientSession client)
        {
            if (client == null)
            {
                return;
            }

            if (!_trackCurrentWaitCycle || _currentWaitCycleStartTimestamp < 0)
            {
                client.CurrentCycleWaitMs = 0d;
                client.HasSubmittedRequestThisCycle = true;
                return;
            }

            var elapsedMs = GetElapsedMilliseconds(_currentWaitCycleStartTimestamp, Stopwatch.GetTimestamp());
            client.CurrentCycleWaitMs = elapsedMs;
            client.HasSubmittedRequestThisCycle = true;
            client.TotalRequestWaitMs += elapsedMs;
            client.RecordedRequestCount += 1;
        }

        private static string ResolveClientDisplayName_NoLock(ClientSession client, string teamTag)
        {
            if (!string.IsNullOrWhiteSpace(client?.TeamName))
            {
                return client.TeamName;
            }

            if (!string.IsNullOrWhiteSpace(teamTag))
            {
                return $"{teamTag} Team";
            }

            return client == null ? "Pending" : $"Client {client.Index + 1}";
        }

        private static double GetElapsedMilliseconds(long startTimestamp, long endTimestamp)
        {
            if (startTimestamp < 0 || endTimestamp < startTimestamp)
            {
                return 0d;
            }

            return (endTimestamp - startTimestamp) * 1000d / Stopwatch.Frequency;
        }

        private static HttpResponseData CreateBinaryResponse(int statusCode, byte[] body)
        {
            return new HttpResponseData(statusCode, "application/x-protobuf", body ?? Array.Empty<byte>());
        }

        private static HttpResponseData CreateErrorResponse(int statusCode, string message = null)
        {
            return new HttpResponseData(statusCode, "text/plain", string.IsNullOrEmpty(message) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(message));
        }

        private static async Task<HttpRequestData> ReadRequestAsync(NetworkStream stream)
        {
            var readBuffer = new byte[4096];
            using var buffer = new MemoryStream();
            var headerEnd = -1;

            while (headerEnd < 0)
            {
                var bytesRead = await stream.ReadAsync(readBuffer, 0, readBuffer.Length);
                if (bytesRead <= 0)
                {
                    return null;
                }

                buffer.Write(readBuffer, 0, bytesRead);
                headerEnd = FindHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
                if (buffer.Length > 64 * 1024)
                {
                    throw new InvalidDataException("HTTP headers are too large.");
                }
            }

            var allBytes = buffer.ToArray();
            var headerText = Encoding.ASCII.GetString(allBytes, 0, headerEnd);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                throw new InvalidDataException("Missing HTTP request line.");
            }

            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2)
            {
                throw new InvalidDataException("Invalid HTTP request line.");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var separatorIndex = line.IndexOf(':');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separatorIndex).Trim();
                var value = line.Substring(separatorIndex + 1).Trim();
                headers[key] = value;
            }

            var bodyOffset = headerEnd + 4;
            var initialBodyLength = allBytes.Length - bodyOffset;
            var contentLength = headers.TryGetValue("Content-Length", out var contentLengthValue)
                ? int.Parse(contentLengthValue)
                : 0;
            var body = new byte[contentLength];

            if (initialBodyLength > 0)
            {
                Buffer.BlockCopy(allBytes, bodyOffset, body, 0, Math.Min(initialBodyLength, contentLength));
            }

            var remaining = contentLength - initialBodyLength;
            var writeOffset = Math.Max(initialBodyLength, 0);
            while (remaining > 0)
            {
                var bytesRead = await stream.ReadAsync(body, writeOffset, remaining);
                if (bytesRead <= 0)
                {
                    throw new EndOfStreamException("Unexpected end of stream while reading request body.");
                }

                writeOffset += bytesRead;
                remaining -= bytesRead;
            }

            return new HttpRequestData(requestLine[0], requestLine[1], headers, body);
        }

        private static int FindHeaderEnd(byte[] bytes, int length)
        {
            for (var i = 0; i <= length - 4; i++)
            {
                if (bytes[i] == '\r' &&
                    bytes[i + 1] == '\n' &&
                    bytes[i + 2] == '\r' &&
                    bytes[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private static async Task WriteResponseAsync(NetworkStream stream, HttpResponseData response)
        {
            var body = response.Body ?? Array.Empty<byte>();
            var headerBuilder = new StringBuilder();
            headerBuilder.Append("HTTP/1.1 ")
                .Append(response.StatusCode)
                .Append(' ')
                .Append(GetReasonPhrase(response.StatusCode))
                .Append("\r\n");
            headerBuilder.Append("Connection: close\r\n");
            headerBuilder.Append("Content-Type: ")
                .Append(response.ContentType ?? "application/octet-stream")
                .Append("\r\n");
            headerBuilder.Append("Content-Length: ")
                .Append(body.Length)
                .Append("\r\n\r\n");

            var headerBytes = Encoding.ASCII.GetBytes(headerBuilder.ToString());
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
            if (body.Length > 0)
            {
                await stream.WriteAsync(body, 0, body.Length);
            }

            await stream.FlushAsync();
        }

        private static string GetReasonPhrase(int statusCode)
        {
            return statusCode switch
            {
                200 => "OK",
                400 => "Bad Request",
                404 => "Not Found",
                405 => "Method Not Allowed",
                409 => "Conflict",
                503 => "Service Unavailable",
                _ => "Error"
            };
        }

        public void Dispose()
        {
            List<TaskCompletionSource<ProtoGameState>> responseSources;
            lock (_messageLock)
            {
                _isRunning = false;
                responseSources = new List<TaskCompletionSource<ProtoGameState>>(_pendingStepRequests.Count + _activeStepRequests.Count);

                foreach (var pending in _pendingStepRequests.Values)
                {
                    responseSources.Add(pending.ResponseSource);
                }

                foreach (var pending in _activeStepRequests.Values)
                {
                    responseSources.Add(pending.ResponseSource);
                }

                _pendingStepRequests.Clear();
                _activeStepRequests.Clear();
                Monitor.PulseAll(_messageLock);
            }

            foreach (var responseSource in responseSources)
            {
                responseSource.TrySetCanceled();
            }

            try
            {
                _tcpListener.Stop();
            }
            catch
            {
            }

            _listenerThread.Join();
        }

        private sealed class HttpRequestData
        {
            public HttpRequestData(string method, string path, Dictionary<string, string> headers, byte[] body)
            {
                Method = method ?? string.Empty;
                Path = path ?? string.Empty;
                Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Body = body ?? Array.Empty<byte>();
            }

            public string Method { get; }
            public string Path { get; }
            public Dictionary<string, string> Headers { get; }
            public byte[] Body { get; }
        }

        private sealed class HttpResponseData
        {
            public HttpResponseData(int statusCode, string contentType, byte[] body)
            {
                StatusCode = statusCode;
                ContentType = contentType;
                Body = body ?? Array.Empty<byte>();
            }

            public int StatusCode { get; }
            public string ContentType { get; }
            public byte[] Body { get; }
        }

        private sealed class ClientSession
        {
            public ClientSession(string clientId, int index)
            {
                ClientId = clientId;
                Index = index;
            }

            public string ClientId { get; }
            public int Index { get; }
            public string TeamName { get; set; } = string.Empty;
            public double TotalRequestWaitMs { get; set; }
            public int RecordedRequestCount { get; set; }
            public bool HasSubmittedRequestThisCycle { get; set; }
            public double CurrentCycleWaitMs { get; set; }

            public void BeginWaitCycle()
            {
                HasSubmittedRequestThisCycle = false;
                CurrentCycleWaitMs = 0d;
            }

            public void ResetRequestMetrics()
            {
                TotalRequestWaitMs = 0d;
                RecordedRequestCount = 0;
                HasSubmittedRequestThisCycle = false;
                CurrentCycleWaitMs = 0d;
            }
        }

        private sealed class PendingStepRequest
        {
            public PendingStepRequest(List<PacManAction> actions, TaskCompletionSource<ProtoGameState> responseSource)
            {
                Actions = actions ?? new List<PacManAction>();
                ResponseSource = responseSource;
            }

            public List<PacManAction> Actions { get; }
            public TaskCompletionSource<ProtoGameState> ResponseSource { get; }
        }
    }
}
