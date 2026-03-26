using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;
using Google.Protobuf;
using PacMan.Interface.PacMan;
using PacMan.Network.Generated;
using UnityEngine;
using ProtoGameState = PacMan.Network.Generated.GameState;

namespace PacMan.Network
{
    public class CommunicatorHttpClient : IDisposable
    {
        public static int channel = 50000;
        public const string TeamNameHeaderName = "X-PacMan-Team-Name";

        private static readonly Lazy<CommunicatorHttpClient> _sLazy = new(() => new CommunicatorHttpClient());

        public static CommunicatorHttpClient Instance => _sLazy.Value;
        public static bool IsInitialized => _sLazy.IsValueCreated;

        public string ClientId { get; private set; }

        private readonly HttpClient _httpClient;
        private readonly object _requestTimingLock = new();
        private string _teamName = string.Empty;
        private double _trackedStepWaitTotalMs;
        private int _trackedStepWaitCount;
        private int _completedStepRequestCount;
        private long _currentTrackedStepStartTimestamp = -1;
        private bool _isCurrentStepTracked;

        private CommunicatorHttpClient()
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{channel}/")
            };
        }

        public string TeamName
        {
            get
            {
                lock (_requestTimingLock)
                {
                    return _teamName;
                }
            }
            set
            {
                lock (_requestTimingLock)
                {
                    _teamName = value?.Trim() ?? string.Empty;
                }
            }
        }

        public double AverageStepRequestWaitMs
        {
            get
            {
                lock (_requestTimingLock)
                {
                    return _trackedStepWaitCount > 0
                        ? _trackedStepWaitTotalMs / _trackedStepWaitCount
                        : 0d;
                }
            }
        }

        public double CurrentStepRequestWaitMs
        {
            get
            {
                lock (_requestTimingLock)
                {
                    if (!_isCurrentStepTracked || _currentTrackedStepStartTimestamp < 0)
                    {
                        return 0d;
                    }

                    return GetElapsedMilliseconds(_currentTrackedStepStartTimestamp, Stopwatch.GetTimestamp());
                }
            }
        }

        public void ResetStepRequestTiming()
        {
            lock (_requestTimingLock)
            {
                _trackedStepWaitTotalMs = 0d;
                _trackedStepWaitCount = 0;
                _completedStepRequestCount = 0;
                _currentTrackedStepStartTimestamp = -1;
                _isCurrentStepTracked = false;
            }
        }

        public ProtoGameState Initialize()
        {
            return InitializeAsync().GetAwaiter().GetResult();
        }

        public async Task<ProtoGameState> InitializeAsync()
        {
            var request = new InitializeRequest();
            if (!string.IsNullOrWhiteSpace(ClientId))
            {
                request.ClientId = ClientId;
            }

            var responseState = await PostMessageAsync("initialize/", request.ToByteArray(), false);
            if (responseState != null)
            {
                ClientId = responseState.ClientId;
            }

            return responseState;
        }

        public ProtoGameState Step(IReadOnlyList<PacManAction> actions)
        {
            return StepAsync(actions).GetAwaiter().GetResult();
        }

        public Task<ProtoGameState> StepAsync(IReadOnlyList<PacManAction> actions)
        {
            if (string.IsNullOrWhiteSpace(ClientId))
            {
                throw new InvalidOperationException("Client must be initialized before stepping.");
            }

            var request = new StepRequest
            {
                ClientId = ClientId
            };

            if (actions != null)
            {
                foreach (var action in actions)
                {
                    request.Actions.Add(new ProtoPacManAction
                    {
                        Acceleration = new ProtoVector2
                        {
                            X = action.Acceleration.x,
                            Y = action.Acceleration.y
                        },
                        Index = action.Index
                    });
                }
            }

            return PostMessageAsync("step/", request.ToByteArray(), true);
        }

        private async Task<ProtoGameState> PostMessageAsync(string path, byte[] payload, bool trackStepRequestTiming)
        {
            var trackedRequestStartedAt = -1L;
            var isTrackedRequest = false;
            if (trackStepRequestTiming)
            {
                lock (_requestTimingLock)
                {
                    isTrackedRequest = _completedStepRequestCount > 0;
                    _isCurrentStepTracked = isTrackedRequest;
                    _currentTrackedStepStartTimestamp = isTrackedRequest ? Stopwatch.GetTimestamp() : -1;
                    trackedRequestStartedAt = _currentTrackedStepStartTimestamp;
                }
            }

            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = content
            };

            var teamName = TeamName;
            if (!string.IsNullOrWhiteSpace(teamName))
            {
                request.Headers.TryAddWithoutValidation(TeamNameHeaderName, teamName);
            }

            try
            {
                using var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var responseBytes = await response.Content.ReadAsByteArrayAsync();
                return responseBytes.Length == 0 ? null : ProtoGameState.Parser.ParseFrom(responseBytes);
            }
            catch (Exception e)
            {
                Debug.LogError($"HTTP client request failed for {path}: {e}");
                return null;
            }
            finally
            {
                if (trackStepRequestTiming)
                {
                    CompleteTrackedStepRequest(isTrackedRequest, trackedRequestStartedAt);
                }
            }
        }

        private void CompleteTrackedStepRequest(bool isTrackedRequest, long trackedRequestStartedAt)
        {
            lock (_requestTimingLock)
            {
                if (isTrackedRequest && trackedRequestStartedAt >= 0)
                {
                    _trackedStepWaitTotalMs += GetElapsedMilliseconds(trackedRequestStartedAt, Stopwatch.GetTimestamp());
                    _trackedStepWaitCount += 1;
                }

                _completedStepRequestCount += 1;
                if (_currentTrackedStepStartTimestamp == trackedRequestStartedAt)
                {
                    _currentTrackedStepStartTimestamp = -1;
                }

                _isCurrentStepTracked = false;
            }
        }

        private static double GetElapsedMilliseconds(long startTimestamp, long endTimestamp)
        {
            if (startTimestamp < 0 || endTimestamp < startTimestamp)
            {
                return 0d;
            }

            return (endTimestamp - startTimestamp) * 1000d / Stopwatch.Frequency;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
