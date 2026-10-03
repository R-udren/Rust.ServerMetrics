using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace RustServerMetrics;

internal class ReportUploader : MonoBehaviour
{
    private readonly Queue<string> _sendBuffer = new(UploadPolicy.BufferCapacity);
    private readonly StringBuilder _payloadBuilder = new();
    private readonly WaitForSecondsRealtime _coalesceDelay = new(UploadPolicy.CoalesceSeconds);
    private readonly Action _notifyFailuresAction;
    private char[] _charBuffer = new char[8192 * 4];
    private MetricsLogger _metricsLogger;
    private UnityWebRequest _activeRequest;
    private int _activeBatchReports;
    private bool _isRunning;
    private bool _throttleErrors;
    private uint _accumulatedErrors;

    public bool IsRunning => _isRunning;
    public int BufferSize => _sendBuffer.Count;
    public long DroppedReports { get; private set; }
    public long FailedBatches { get; private set; }
    public long SentBatches { get; private set; }

    public ReportUploader()
    {
        _notifyFailuresAction = NotifyFailures;
    }

    private void Awake()
    {
        _metricsLogger = GetComponent<MetricsLogger>();
        if (_metricsLogger != null)
            return;

        Debug.LogError("[ServerMetrics]: ReportUploader failed to find its MetricsLogger.");
        Destroy(this);
    }

    public void AddToSendBuffer(string payload)
    {
        if (!_metricsLogger.Ready || string.IsNullOrEmpty(payload))
            return;

        if (_sendBuffer.Count == UploadPolicy.BufferCapacity)
        {
            _sendBuffer.Dequeue();
            DroppedReports++;
        }
        _sendBuffer.Enqueue(payload);
        if (_isRunning)
            return;

        _isRunning = true;
        StartCoroutine(SendBufferLoop());
    }

    private IEnumerator SendBufferLoop()
    {
        try
        {
            while (_isRunning && _sendBuffer.Count > 0)
            {
                var batchSize = _metricsLogger.Configuration.BatchSize;
                if (UploadPolicy.ShouldCoalesce(_sendBuffer.Count, batchSize))
                    yield return _coalesceDelay;
                if (!_isRunning || !_metricsLogger.Ready)
                    yield break;

                _activeBatchReports = Math.Min(_sendBuffer.Count, batchSize);
                _payloadBuilder.Clear();
                for (var i = 0; i < _activeBatchReports; i++)
                    _payloadBuilder.Append(_sendBuffer.Dequeue()).Append('\n');

                if (_payloadBuilder.Length > _charBuffer.Length)
                    _charBuffer = new char[_payloadBuilder.Length + 1024];
                _payloadBuilder.CopyTo(0, _charBuffer, 0, _payloadBuilder.Length);
                var data = Encoding.UTF8.GetBytes(_charBuffer, 0, _payloadBuilder.Length);
                _payloadBuilder.Clear();
                yield return SendRequest(data, _metricsLogger.BaseUri, _metricsLogger.AuthorizationHeader);
                _activeBatchReports = 0;
            }
        }
        finally
        {
            _isRunning = false;
        }
    }

    private IEnumerator SendRequest(byte[] data, Uri uri, string authorization)
    {
        for (var attempt = 1; attempt <= UploadPolicy.MaximumAttempts && _isRunning; attempt++)
        {
            var request = new UnityWebRequest(uri, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(data),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 15,
                redirectLimit = 0
            };
            request.SetRequestHeader("Authorization", authorization);
            request.SetRequestHeader("Content-Type", "text/plain; charset=utf-8");
            _activeRequest = request;
            bool networkError;
            bool success;
            long status;
            string error;
            string response;
            try
            {
                yield return request.SendWebRequest();
                networkError = request.result == UnityWebRequest.Result.ConnectionError;
                status = request.responseCode;
                success = status >= 200 && status < 300 && request.result == UnityWebRequest.Result.Success;
                error = request.error;
                response = _metricsLogger.Configuration?.DebugLogging == true ? request.downloadHandler.text : null;
            }
            finally
            {
                DisposeActiveRequest();
            }

            if (success)
            {
                SentBatches++;
                _activeBatchReports = 0;
                yield break;
            }
            if (UploadPolicy.ShouldRetry(networkError, status, attempt))
            {
                yield return new WaitForSecondsRealtime(attempt);
                continue;
            }

            FailedBatches++;
            DroppedReports += _activeBatchReports;
            _activeBatchReports = 0;
            ReportFailure(status, error, response);
            yield break;
        }
    }

    private void ReportFailure(long status, string error, string response)
    {
        if (_throttleErrors)
        {
            _accumulatedErrors++;
            return;
        }
        Debug.LogError($"[ServerMetrics]: Metrics batch discarded after upload failure (HTTP {status}): {error}");
        if (response != null)
            Debug.LogError(response);
        _throttleErrors = true;
        InvokeHandler.Invoke(this, _notifyFailuresAction, 5);
    }

    private void NotifyFailures()
    {
        _throttleErrors = false;
        if (_accumulatedErrors == 0)
            return;
        Debug.LogError($"[ServerMetrics]: {_accumulatedErrors} additional batch failures in the last five seconds.");
        _accumulatedErrors = 0;
    }

    private void DisposeActiveRequest()
    {
        if (_activeRequest == null)
            return;
        _activeRequest.Dispose();
        _activeRequest = null;
    }

    private void OnDestroy() => Stop();

    public void Stop()
    {
        _isRunning = false;
        _activeRequest?.Abort();
        DisposeActiveRequest();
        StopAllCoroutines();
        InvokeHandler.CancelInvoke(this, _notifyFailuresAction);
        DroppedReports += _sendBuffer.Count + _activeBatchReports;
        _sendBuffer.Clear();
        _payloadBuilder.Clear();
        _activeBatchReports = 0;
        _throttleErrors = false;
        _accumulatedErrors = 0;
    }
}
