using System.Text.Json;
using System.Text.Json.Serialization;

namespace crud_app_backend.Bot.Models
{
    /// <summary>
    /// All conversation state for one user.
    /// Stored as JSON inside WhatsAppSession.TempData column.
    ///
    /// Handles TWO TempData formats:
    ///   1. camelCase  — saved by .NET BotService  (staffId, staffName, ...)
    ///   2. snake_case — saved by n8n workflow      (staff_id, staff_name, ...)
    /// </summary>
    public class BotSession
    {
        public string Phone { get; set; } = string.Empty;
        public string State { get; set; } = "INIT";
        public string PreviousState { get; set; } = "INIT";
        public string? Lang { get; set; }

        // ── Staff ─────────────────────────────────────────────────────────────
        public bool StaffVerified { get; set; }
        public string? StaffId { get; set; }
        public string? StaffName { get; set; }
        public string? StaffContact { get; set; }
        public string? StaffEmail { get; set; }
        public string? StaffDesignation { get; set; }
        public string? StaffDept { get; set; }
        public string? StaffGroup { get; set; }
        public string? StaffCompany { get; set; }
        public string? StaffLocation { get; set; }

        // ── Complaint ─────────────────────────────────────────────────────────
        public string ComplaintDescription { get; set; } = string.Empty;
        public List<string> ComplaintVoices { get; set; } = new();
        public List<string> ComplaintImages { get; set; } = new();
        public string? ComplaintId { get; set; }

        // ── Helpers ───────────────────────────────────────────────────────────
        public string T(string en, string bn) => Lang == "bn" ? bn : en;

        // ─────────────────────────────────────────────────────────────────────
        // SERIALIZATION
        // ─────────────────────────────────────────────────────────────────────

        private static readonly JsonSerializerOptions _writeOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>
        /// Serialize to camelCase JSON for storage in TempData.
        /// </summary>
        public string Save() => JsonSerializer.Serialize(this, _writeOpts);

        /// <summary>
        /// Load session from TempData JSON.
        /// Handles both camelCase (.NET) and snake_case (n8n) formats.
        /// </summary>
        public static BotSession Load(string phone, string? tempDataJson)
        {
            var s = new BotSession { Phone = phone };

            if (string.IsNullOrWhiteSpace(tempDataJson) || tempDataJson == "{}")
                return s;

            try
            {
                using var doc = JsonDocument.Parse(tempDataJson);
                var root = doc.RootElement;

                // ── State ─────────────────────────────────────────────────────
                // .NET saves: "state", n8n saves: "state" (same)
                s.State = Str(root, "state") ?? "INIT";
                s.PreviousState = Str(root, "previousState")
                               ?? Str(root, "previous_state") ?? "INIT";
                s.Lang = Str(root, "lang");

                if (string.IsNullOrWhiteSpace(s.State)) s.State = "INIT";

                // ── Staff verified ────────────────────────────────────────────
                // .NET: "staffVerified", n8n: "staff_verified"
                s.StaffVerified = Bool(root, "staffVerified")
                               ?? Bool(root, "staff_verified")
                               ?? false;

                // ── Staff fields — try camelCase first, then snake_case ────────
                s.StaffId = Str(root, "staffId") ?? Str(root, "staff_id");
                s.StaffName = Str(root, "staffName") ?? Str(root, "staff_name");
                s.StaffContact = Str(root, "staffContact") ?? Str(root, "staff_contact");
                s.StaffEmail = Str(root, "staffEmail") ?? Str(root, "staff_email");
                s.StaffDesignation = Str(root, "staffDesignation") ?? Str(root, "staff_designation");
                s.StaffDept = Str(root, "staffDept") ?? Str(root, "staff_dept");
                s.StaffGroup = Str(root, "staffGroup") ?? Str(root, "staff_group");
                s.StaffCompany = Str(root, "staffCompany") ?? Str(root, "staff_company");
                s.StaffLocation = Str(root, "staffLocation") ?? Str(root, "staff_location");

                // ── Complaint fields ──────────────────────────────────────────
                s.ComplaintDescription = Str(root, "complaintDescription")
                                      ?? Str(root, "complaint_description")
                                      ?? string.Empty;
                s.ComplaintId = Str(root, "complaintId")
                                      ?? Str(root, "complaint_id");

                s.ComplaintVoices = StrList(root, "complaintVoices")
                                 ?? StrList(root, "complaint_voices")
                                 ?? new();
                s.ComplaintImages = StrList(root, "complaintImages")
                                 ?? StrList(root, "complaint_images")
                                 ?? new();
            }
            catch
            {
                // Corrupt JSON — return fresh session, user will re-verify
                s = new BotSession { Phone = phone };
            }

            return s;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Private helpers
        // ─────────────────────────────────────────────────────────────────────

        private static string? Str(JsonElement el, string key)
        {
            if (el.TryGetProperty(key, out var v) &&
                v.ValueKind != JsonValueKind.Null)
                return v.GetString();
            return null;
        }

        private static bool? Bool(JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }

        private static List<string>? StrList(JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var v) ||
                v.ValueKind != JsonValueKind.Array)
                return null;

            return v.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? "")
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList();
        }
    }
}
