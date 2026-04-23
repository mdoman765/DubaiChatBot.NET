using System.Text.Json;
using System.Text.Json.Serialization;

namespace crud_app_backend.Bot.Models
{
    // ── Cart item stored in session ───────────────────────────────────────────
    public class UaeCartItem
    {
        public string Pid { get; set; } = "";
        public string Name { get; set; } = "";
        public double Price { get; set; }
        public int Qty { get; set; }
        public string ProductCode { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public double OldPrice { get; set; }
        public int Factor { get; set; } = 1;
        public string GroupId { get; set; } = "";  // from product API — used in order
        public string PriceId { get; set; } = "";  // from product API — used in order
        public double Amount => Math.Round(Price * Qty, 3);
    }

    // ── Full session state for one UAE bot user ───────────────────────────────
    public class UaeSession
    {
        public string Phone { get; set; } = string.Empty;
        public string State { get; set; } = "INIT";
        public string PreviousState { get; set; } = "INIT";
        public string? Lang { get; set; }   // "en" | "bn" | "hi"

        // Shop authentication
        public bool ShopVerified { get; set; }
        public string? ShopCode { get; set; }
        public string? ShopUserId { get; set; }
        public string? ShopName { get; set; }
        public string? ShopCountryId { get; set; }
        public string? ShopGroupId { get; set; }
        public string? ShopPriceId { get; set; }

        // Order flow — menu number → command mapping
        public Dictionary<string, string> MenuMap { get; set; } = new();

        // Order flow — browsing context
        public string? SelectedCatId { get; set; }
        public string? SelectedCatName { get; set; }
        public string? SelectedSubcatId { get; set; }
        public string? SelectedSubcatName { get; set; }
        public string? PendingCartPid { get; set; }
        public string? PendingCartName { get; set; }
        public double PendingCartPrice { get; set; }

        // Cart
        public List<UaeCartItem> Cart { get; set; } = new();

        // Complaint / Return media
        public string MediaDescription { get; set; } = string.Empty;
        public List<string> MediaImages { get; set; } = new();
        public List<string> MediaVoices { get; set; } = new();

        // Agent confirm — tracks which step of double-confirm we're on
        public int AgentConfirmStep { get; set; } = 0;

        // ── Language helper ───────────────────────────────────────────────────
        public string T(string en, string bn, string hi)
            => Lang == "bn" ? bn : Lang == "hi" ? hi : en;

        public string T(string en, string bn)
            => Lang == "bn" ? bn : en;

        // ── Serialisation ─────────────────────────────────────────────────────
        private static readonly JsonSerializerOptions WriteOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public string Save() => JsonSerializer.Serialize(this, WriteOpts);

        public static UaeSession Load(string phone, string? json)
        {
            var s = new UaeSession { Phone = phone };
            if (string.IsNullOrWhiteSpace(json) || json == "{}") return s;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                s.State = Str(root, "state") ?? "INIT";
                s.PreviousState = Str(root, "previousState") ?? "INIT";
                s.Lang = Str(root, "lang");

                s.ShopVerified = Bool(root, "shopVerified") ?? false;
                s.ShopCode = Str(root, "shopCode");
                s.ShopUserId = Str(root, "shopUserId");
                s.ShopName = Str(root, "shopName");
                s.ShopCountryId = Str(root, "shopCountryId");
                s.ShopGroupId = Str(root, "shopGroupId");
                s.ShopPriceId = Str(root, "shopPriceId");

                // MenuMap
                if (root.TryGetProperty("menuMap", out var mm) &&
                    mm.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in mm.EnumerateObject())
                        s.MenuMap[prop.Name] = prop.Value.GetString() ?? "";
                }

                s.SelectedCatId = Str(root, "selectedCatId");
                s.SelectedCatName = Str(root, "selectedCatName");
                s.SelectedSubcatId = Str(root, "selectedSubcatId");
                s.SelectedSubcatName = Str(root, "selectedSubcatName");
                s.PendingCartPid = Str(root, "pendingCartPid");
                s.PendingCartName = Str(root, "pendingCartName");

                if (root.TryGetProperty("pendingCartPrice", out var pcp))
                    s.PendingCartPrice = pcp.GetDouble();

                // Cart
                if (root.TryGetProperty("cart", out var cartEl) &&
                    cartEl.ValueKind == JsonValueKind.Array)
                {
                    s.Cart = JsonSerializer.Deserialize<List<UaeCartItem>>(
                        cartEl.GetRawText(), WriteOpts) ?? new();
                }

                s.MediaDescription = Str(root, "mediaDescription") ?? string.Empty;
                s.MediaImages = StrList(root, "mediaImages") ?? new();
                s.MediaVoices = StrList(root, "mediaVoices") ?? new();
                s.AgentConfirmStep = root.TryGetProperty("agentConfirmStep", out var acs)
                    ? acs.GetInt32() : 0;
            }
            catch { s = new UaeSession { Phone = phone }; }

            return s;
        }

        // ── JSON helpers ──────────────────────────────────────────────────────
        private static string? Str(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
               ? v.GetString() : null;

        private static bool? Bool(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True
                ? true
                : el.TryGetProperty(key, out var v2) && v2.ValueKind == JsonValueKind.False
                    ? false : null;

        private static List<string>? StrList(JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
                if (item.GetString() is { } s) list.Add(s);
            return list;
        }
    }
}
