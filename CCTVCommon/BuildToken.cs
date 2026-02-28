namespace CCTVCommon
{
    /// <summary>
    /// Shared build token used for HMAC challenge-response handshake.
    /// Both CCTVPlugin and CCTVCapture.exe compile this from the same source,
    /// so a modified third-party binary will not know the correct value.
    /// Update this GUID whenever you publish a new release.
    /// </summary>
    public static class BuildToken
    {
        public const string Value = "f3a7c291-84be-4d10-b62e-1a9305fe7d48";
    }
}
