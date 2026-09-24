using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Gnb.Clocking.Application.Clocking;

namespace Gnb.Clocking.Infrastructure.ClockKiosk;

/// <summary>
/// HTTP calls to gnbSaasApi <c>api/clock-kiosk</c>. Every failure surfaces as <see cref="ClockingException"/>
/// so the kiosk banner shows the server's message (400) or a plain connectivity message.
/// </summary>
public sealed class ClockKioskApiClient
{
    private const string NotConfiguredMessage =
        "The clock server is not configured. Set ClockKiosk:BaseUrl and ClockKiosk:ApiKey.";
    private const string UnreachableMessage =
        "Cannot reach the GroupNB clock server. Check the network and try again.";
    private const string UnknownBadgeMessage = "No GroupNB candidate is linked to this badge.";

    private readonly HttpClient _http;
    private readonly bool _configured;

    public ClockKioskApiClient(HttpClient http, ClockKioskApiOptions options)
    {
        _http = http;
        _configured = options.IsConfigured;
        if (!_configured)
            return;

        var baseUrl = options.BaseUrl!.Trim();
        _http.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        _http.Timeout = options.Timeout;
        _http.DefaultRequestHeaders.Remove(ClockKioskApiOptions.KeyHeaderName);
        _http.DefaultRequestHeaders.Add(ClockKioskApiOptions.KeyHeaderName, options.ApiKey!.Trim());
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Null when the badge is unknown or outside the GroupNB allowlist (API 404).</summary>
    internal async Task<BadgeResponse?> GetBadgeAsync(string rfid, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"api/clock-kiosk/badges/{Uri.EscapeDataString(rfid)}?utc_offset_minutes={DateTimeOffset.Now.Offset.TotalMinutes.ToString(CultureInfo.InvariantCulture)}"),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        return await ReadAsync<BadgeResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PhotoUploadResponse> UploadPhotoAsync(
        string rfid,
        bool isClockOut,
        string localJpegPath,
        CancellationToken cancellationToken)
    {
        var jpeg = await File.ReadAllBytesAsync(localJpegPath, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(() =>
        {
            var photo = new ByteArrayContent(jpeg);
            photo.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            var form = new MultipartFormDataContent
            {
                { photo, "photo", ClockPhotoPaths.FileName(isClockOut) },
                { new StringContent(rfid), "rfid" },
                { new StringContent(isClockOut ? "true" : "false"), "is_clock_out" },
            };
            return new HttpRequestMessage(HttpMethod.Post, "api/clock-kiosk/photos") { Content = form };
        }, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<PhotoUploadResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ClockActionResponse> ClockAsync(
        bool isClockOut,
        string rfid,
        string imagePath,
        bool resetCompletedDay,
        CancellationToken cancellationToken)
    {
        var route = isClockOut ? "api/clock-kiosk/clock-out" : "api/clock-kiosk/clock-in";
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, route)
            {
                Content = JsonContent.Create(new ClockRequest(rfid, imagePath, resetCompletedDay, DateTimeOffset.Now)),
            },
            cancellationToken).ConfigureAwait(false);

        return await ReadAsync<ClockActionResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SessionsResponse> GetSessionsAsync(int take, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"api/clock-kiosk/sessions?take={take.ToString(CultureInfo.InvariantCulture)}"),
            cancellationToken).ConfigureAwait(false);

        return await ReadAsync<SessionsResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        if (!_configured)
            throw new ClockingException(NotConfiguredMessage);

        using var request = createRequest();
        try
        {
            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new ClockingException(UnreachableMessage);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClockingException(UnreachableMessage);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var serverMessage = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        var message = response.StatusCode switch
        {
            HttpStatusCode.NotFound => UnknownBadgeMessage,
            HttpStatusCode.Unauthorized => "The clock server rejected this kiosk's key.",
            HttpStatusCode.TooManyRequests => "The clock server is busy. Try again in a minute.",
            _ when !string.IsNullOrWhiteSpace(serverMessage) => serverMessage,
            _ => $"The clock server returned {(int)response.StatusCode}.",
        };
        throw new ClockingException(message);
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(cancellationToken).ConfigureAwait(false);
            return error?.Error;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false);
            return value ?? throw new ClockingException("The clock server sent an empty response.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ClockingException("The clock server sent a response this kiosk cannot read.");
        }
    }
}
