using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace TransitPass.Core.Api.Payments;

/// <summary>Settings under "Payments".</summary>
public sealed class PaymentOptions
{
    public const string Section = "Payments";

    /// <summary>The name stored with each inbox row, so two providers' event ids can never collide.</summary>
    public string Provider { get; set; } = "fakepay";

    /// <summary>Shared with the provider, used for HMAC. Never in the repo: comes from .env / a secret store.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>How old (or how far in the future) a signed timestamp may be. Limits replays of a captured webhook.</summary>
    public TimeSpan SignatureTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Where the client sends the user to pay; the top-up id is appended.</summary>
    public string CheckoutBaseUrl { get; set; } = "https://pay.example.test/checkout/";
}

/// <summary>The webhook secret as bytes, resolved once at startup.</summary>
public sealed class WebhookSecret
{
    internal const int MinLength = 32;

    public WebhookSecret(IOptions<PaymentOptions> options, IHostEnvironment env, ILogger<WebhookSecret> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(env);
        string? secret = options.Value.WebhookSecret;

        if (string.IsNullOrEmpty(secret))
        {
            if (!env.IsDevelopment() && !env.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException("Payments:WebhookSecret is required outside Development.");
            }

            // Local runs still start. Every webhook will be refused until a real secret is set.
            Bytes = RandomNumberGenerator.GetBytes(32);
            logger.LogWarning("Payments:WebhookSecret is not set; using a random one, so all payment webhooks will be rejected.");
            return;
        }

        if (secret.Length < MinLength)
        {
            throw new InvalidOperationException($"Payments:WebhookSecret must be at least {MinLength} characters.");
        }

        Bytes = Encoding.UTF8.GetBytes(secret);
    }

    public byte[] Bytes { get; }
}
