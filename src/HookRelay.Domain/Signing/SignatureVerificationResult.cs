namespace HookRelay.Domain.Signing;

/// <summary>Why a signature check passed or failed. Every rejection reason is distinct so receivers can log usefully.</summary>
public enum SignatureVerificationResult
{
    /// <summary>The payload is authentic and inside the replay window.</summary>
    Valid = 0,

    /// <summary>The header was missing, empty, or not in <c>t=...,v1=...</c> form.</summary>
    MalformedHeader = 1,

    /// <summary>The signed timestamp is older or newer than the caller's tolerance allows.</summary>
    TimestampOutsideTolerance = 2,

    /// <summary>The header parsed and the timestamp was fresh, but no signature matched.</summary>
    SignatureMismatch = 3,
}
