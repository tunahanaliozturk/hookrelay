using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using HookRelay.Domain.Deliveries;
using HookRelay.Domain.Endpoints;
using HookRelay.Domain.Security;
using HookRelay.Domain.Signing;
using HookRelay.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace HookRelay.Infrastructure.Sending;

/// <summary>The result of one HTTP attempt.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="StatusCode">Response status, when the endpoint answered.</param>
/// <param name="Latency">Wall-clock duration.</param>
/// <param name="ResponseSnippet">Start of the response body.</param>
/// <param name="Error">Failure message, when there was one.</param>
public readonly record struct SendResult(
    AttemptOutcome Outcome,
    int? StatusCode,
    TimeSpan Latency,
    string? ResponseSnippet,
    string? Error)
{
    /// <summary>True when the endpoint answered 2xx.</summary>
    public bool IsSuccess => Outcome is AttemptOutcome.Success;
}

/// <summary>Signs and sends one webhook request.</summary>
public interface IWebhookSender
{
    /// <summary>Sends one attempt. Never throws for a failed delivery: failure comes back as a result.</summary>
    /// <param name="endpoint">Destination.</param>
    /// <param name="delivery">The delivery being attempted.</param>
    /// <param name="secret">The signing secret, already decrypted, matching the delivery's pinned version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SendResult> SendAsync(
        WebhookEndpoint endpoint,
        Delivery delivery,
        string secret,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default sender: URL policy check, HMAC signature, one bounded HTTP POST.
/// </summary>
/// <remarks>
/// Everything that could take unbounded time is bounded here rather than left to the caller. The request
/// has its own timeout, the response body is only read up to the configured snippet size, and redirects
/// are refused outright, because following one is how a destination that passed the URL check turns into
/// a request against something that never would have.
/// </remarks>
public sealed class WebhookSender(
    IHttpClientFactory httpClientFactory,
    IOptions<DeliveryOptions> options,
    TimeProvider timeProvider) : IWebhookSender
{
    /// <summary>Name of the configured <see cref="HttpClient"/>.</summary>
    public const string HttpClientName = "hookrelay-delivery";

    private readonly IHttpClientFactory _httpClientFactory =
        httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

    private readonly DeliveryOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public async Task<SendResult> SendAsync(
        WebhookEndpoint endpoint,
        Delivery delivery,
        string secret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentException.ThrowIfNullOrEmpty(secret);

        UrlValidationResult urlCheck = WebhookUrlPolicy.Validate(
            endpoint.Url,
            _options.AllowInsecureHttp,
            _options.AllowPrivateNetworkDestinations);

        if (urlCheck is not UrlValidationResult.Allowed)
        {
            return new SendResult(
                AttemptOutcome.BlockedByPolicy,
                StatusCode: null,
                TimeSpan.Zero,
                ResponseSnippet: null,
                $"Destination rejected by URL policy: {urlCheck}.");
        }

        byte[] body = Encoding.UTF8.GetBytes(delivery.PayloadJson);
        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        string signature = WebhookSignature.Compute(secretBytes, _timeProvider.GetUtcNow(), body);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
        {
            Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" } },
            },
        };

        request.Headers.TryAddWithoutValidation(WebhookSignature.HeaderName, signature);
        request.Headers.TryAddWithoutValidation(
            WebhookSignature.DeliveryIdHeaderName,
            delivery.Id.ToString("D"));
        request.Headers.TryAddWithoutValidation(WebhookSignature.EventTypeHeaderName, delivery.EventType);
        request.Headers.TryAddWithoutValidation(
            WebhookSignature.AttemptHeaderName,
            (delivery.AttemptCount + 1).ToString(provider: null));
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);

        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        long startedAt = _timeProvider.GetTimestamp();

        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            string? snippet = await ReadSnippetAsync(response, timeout.Token);
            TimeSpan latency = _timeProvider.GetElapsedTime(startedAt);
            int status = (int)response.StatusCode;

            return response.IsSuccessStatusCode
                ? new SendResult(AttemptOutcome.Success, status, latency, snippet, Error: null)
                : new SendResult(
                    AttemptOutcome.HttpError,
                    status,
                    latency,
                    snippet,
                    $"Endpoint responded {status} {response.ReasonPhrase}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SendResult(
                AttemptOutcome.Timeout,
                StatusCode: null,
                _timeProvider.GetElapsedTime(startedAt),
                ResponseSnippet: null,
                $"No response within {_options.RequestTimeout.TotalSeconds:0.###}s.");
        }
        catch (HttpRequestException exception)
        {
            return new SendResult(
                AttemptOutcome.NetworkError,
                StatusCode: null,
                _timeProvider.GetElapsedTime(startedAt),
                ResponseSnippet: null,
                exception.Message);
        }
        finally
        {
            Array.Clear(secretBytes);
        }
    }

    private async Task<string?> ReadSnippetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (_options.ResponseSnippetBytes <= 0)
        {
            return null;
        }

        // A destination is under someone else's control, so the response body is never read in full.
        byte[] buffer = new byte[_options.ResponseSnippetBytes];
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        if (read == 0)
        {
            return null;
        }

        Debug.Assert(read <= buffer.Length, "Read more than the buffer holds.");
        return Encoding.UTF8.GetString(buffer, 0, read).ReplaceLineEndings(" ");
    }
}
