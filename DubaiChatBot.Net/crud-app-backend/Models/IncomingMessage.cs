namespace crud_app_backend.Bot.Models
{
    /// <summary>Parsed WhatsApp message extracted from a 360dialog webhook body.</summary>
    public class IncomingMessage
    {
        public string From         { get; init; } = string.Empty;
        public string SenderName   { get; init; } = string.Empty;
        public string MessageId    { get; init; } = string.Empty;
        /// <summary>text | audio | image</summary>
        public string MsgType      { get; init; } = string.Empty;
        public long   Timestamp    { get; init; }
        /// <summary>Lowercased, trimmed, zero-width stripped. Empty for non-text.</summary>
        public string RawText      { get; init; } = string.Empty;

        // Audio fields (only when MsgType == "audio")
        public string AudioId      { get; init; } = string.Empty;
        public string AudioMime    { get; init; } = "audio/ogg";

        // Image fields (only when MsgType == "image")
        public string ImageId      { get; init; } = string.Empty;
        public string ImageMime    { get; init; } = "image/jpeg";
        public string ImageCaption { get; init; } = string.Empty;
    }
}
