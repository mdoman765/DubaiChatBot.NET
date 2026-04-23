using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using crud_app_backend.Bot.Models;
using crud_app_backend.DTOs;
using crud_app_backend.Models;
using crud_app_backend.Repositories;
using crud_app_backend.Services;
using Microsoft.Extensions.Caching.Memory;

namespace crud_app_backend.Bot.Services
{
    /// <summary>
    /// UAE WhatsApp bot — Pran-RFL Customer Support (retailer-facing).
    /// Handles: Order, Return, Complaint, Agent, Salesman, Order Tracking.
    /// Languages: English / Bangla / Hindi.
    /// </summary>
    public class UaeBotService : IUaeBotService
    {
        private readonly IWhatsAppSessionService _sessionSvc;
        private readonly IWhatsAppMessageRepository _msgRepo;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly IDialogClient _dialog;
        private readonly ISprorClient _spror;
        private readonly IUaeCrmService _crm;
        private readonly IMemoryCache _cache;
        private readonly BotStateService _state;
        private readonly ILogger<UaeBotService> _logger;

        public UaeBotService(
            IWhatsAppSessionService sessionSvc,
            IWhatsAppMessageRepository msgRepo,
            IWebHostEnvironment env,
            IConfiguration config,
            IDialogClient dialog,
            ISprorClient spror,
            IUaeCrmService crm,
            IMemoryCache cache,
            BotStateService state,
            ILogger<UaeBotService> logger)
        {
            _sessionSvc = sessionSvc;
            _msgRepo = msgRepo;
            _env = env;
            _config = config;
            _dialog = dialog;
            _spror = spror;
            _crm = crm;
            _cache = cache;
            _state = state;
            _logger = logger;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ENTRY POINT
        // ─────────────────────────────────────────────────────────────────────

        public async Task ProcessAsync(JsonElement body)
        {
            try
            {
                var msg = UaeMessageParser.Parse(body);
                if (msg is null) return;

                _logger.LogInformation("[UAE] {Type} from {Phone} id={Id}",
                    msg.MsgType, msg.From, msg.MessageId);

                // Per-user lock — prevents gallery burst race conditions
                var userLock = _state.UserLocks.GetOrAdd(msg.From, _ => new SemaphoreSlim(1, 1));
                await userLock.WaitAsync();
                try
                {
                    var session = await LoadSessionAsync(msg.From);

                    // Instant ACK for slow paths
                    var ack = GetAckMessage(session, msg);
                    if (ack != null)
                        await _dialog.SendTextAsync(msg.From, ack);

                    var reply = await RouteAsync(session, msg);

                    // Always persist — even on empty reply (burst image suppression)
                    if (string.IsNullOrWhiteSpace(reply))
                    {
                        await PersistSessionAsync(session, msg.RawText);
                        return;
                    }

                    await Task.WhenAll(
                        PersistSessionAsync(session, msg.RawText),
                        _dialog.SendTextAsync(msg.From, reply)
                    );
                }
                finally { userLock.Release(); }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UAE] ProcessAsync unhandled crash");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // INSTANT ACK
        // ─────────────────────────────────────────────────────────────────────

        private static string? GetAckMessage(UaeSession s, UaeIncomingMessage msg)
        {
            if (s.State == "AWAITING_SHOP_CODE" && msg.MsgType == "text")
                return s.T("🔍 Verifying shop...", "🔍 শপ যাচাই করা হচ্ছে...", "🔍 दुकान की जाँच हो रही है...");

            // ACK when user is selecting a category — about to load subcategories
            if (s.State == "AWAITING_CATEGORY" && msg.MsgType == "text" &&
                msg.RawText != "0" && !string.IsNullOrEmpty(msg.RawText))
                return s.T("⏳ Loading subcategories...", "⏳ সাবক্যাটাগরি লোড হচ্ছে...", "⏳ उपश्रेणी लोड हो रही है...");

            // ACK when user is selecting a subcategory — about to load products
            if (s.State == "AWAITING_SUBCATEGORY" && msg.MsgType == "text" &&
                msg.RawText != "0" && !string.IsNullOrEmpty(msg.RawText))
                return s.T("⏳ Loading products...", "⏳ পণ্য লোড হচ্ছে...", "⏳ उत्पाद लोड हो रहा है...");

            if ((s.State == "AWAITING_CART_ACTION" || s.State == "AWAITING_CART_VIEW") &&
                msg.RawText == "x")
                return s.T("⏳ Placing your order...", "⏳ অর্ডার দেওয়া হচ্ছে...", "⏳ ऑर्डर दिया जा रहा है...");

            if (s.State == "AWAITING_COMPLAINT_CONFIRM" && msg.RawText == "y")
                return s.T("⏳ Submitting complaint...", "⏳ অভিযোগ জমা হচ্ছে...", "⏳ शिकायत जमा हो रही है...");

            if (s.State == "AWAITING_RETURN_CONFIRM" && msg.RawText == "y")
                return s.T("⏳ Submitting return request...", "⏳ রিটার্ন জমা হচ্ছে...", "⏳ वापसी जमा हो रही है...");

            if (s.State == "AWAITING_AGENT_CONFIRM_1" && msg.RawText == "y")
                return s.T("⏳ Connecting to agent...", "⏳ এজেন্টের সাথে সংযোগ...", "⏳ एजेंट से जोड़ा जा रहा है...");

            if (s.State == "AWAITING_ORDER_TRACKING" && msg.MsgType == "text")
                return s.T("🔍 Looking up orders...", "🔍 অর্ডার খোঁজা হচ্ছে...", "🔍 ऑर्डर खोजे जा रहे हैं...");

            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ROUTER — global shortcuts + state dispatch
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> RouteAsync(UaeSession s, UaeIncomingMessage msg)
        {
            var raw = msg.RawText;

            // ── Global resets ─────────────────────────────────────────────────
            if (msg.MsgType == "text" &&
                new[] { "hi", "hello", "start", "hey", "new" }.Contains(raw))
            {
                ResetSession(s);
                await SendWelcomeAsync(msg.From);
                return string.Empty;
            }

            // ── Must have lang before anything else ───────────────────────────
            // IMPORTANT: check State == "INIT" ONLY.
            // Do NOT add s.Lang == null — when State is AWAITING_LANG,
            // Lang is legitimately null and the state dispatch below handles it.
            // Adding Lang == null here causes an infinite welcome loop:
            //   user sends "1" → state=AWAITING_LANG, lang=null → fires again → loop.
            if (s.State == "INIT")
            {
                Transition(s, "AWAITING_LANG");
                await SendWelcomeAsync(msg.From);
                return string.Empty;
            }

            // ── Must be shop-verified for menu shortcuts ───────────────────────
            if (s.ShopVerified)
            {
                if (msg.MsgType == "text" && raw == "menu")
                    return BuildMainMenu(s);

                if (msg.MsgType == "text" && raw == "s")
                {
                    Transition(s, "AWAITING_AGENT_CONFIRM_1");
                    return BuildAgentConfirm1(s);
                }
            }

            // ── State dispatch ────────────────────────────────────────────────
            return s.State switch
            {
                "AWAITING_LANG" => HandleLang(s, msg),
                "AWAITING_SHOP_CODE" => await HandleShopCodeAsync(s, msg),
                "MAIN_MENU" => await HandleMainMenu(s, msg),
                "AWAITING_CATEGORY" => await HandleCategoryAsync(s, msg),
                "AWAITING_SUBCATEGORY" => await HandleSubcategoryAsync(s, msg),
                "AWAITING_PRODUCT" => HandleProduct(s, msg),
                "AWAITING_QTY" => HandleQty(s, msg),
                "AWAITING_CART_ACTION" => await HandleCartActionAsync(s, msg),
                "AWAITING_CART_VIEW" => await HandleCartViewAsync(s, msg),
                "AWAITING_RETURN_DETAILS" => await HandleMediaDetailsAsync(s, msg, "return"),
                "AWAITING_RETURN_CONFIRM" => await HandleReturnConfirmAsync(s, msg),
                "AWAITING_COMPLAINT_DETAILS" => await HandleMediaDetailsAsync(s, msg, "complaint"),
                "AWAITING_COMPLAINT_CONFIRM" => await HandleComplaintConfirmAsync(s, msg),
                "AWAITING_AGENT_CONFIRM_1" => await HandleAgentConfirm1Async(s, msg),
                "AWAITING_AGENT_CONFIRM_2" => await HandleAgentConfirm1Async(s, msg), // fallback — no second step
                "AWAITING_AREA_INPUT" => HandleAreaInput(s, msg),
                "AWAITING_ORDER_TRACKING" => await HandleOrderTrackingAsync(s, msg),
                _ => BuildMainMenu(s),
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // LANGUAGE SELECTION
        // ─────────────────────────────────────────────────────────────────────

        private string HandleLang(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.MsgType != "text") return LangPrompt();

            switch (msg.RawText.Trim())
            {
                case "1": s.Lang = "en"; break;
                case "2": s.Lang = "bn"; break;
                case "3": s.Lang = "hi"; break;
                default:
                    return
                        "❌ Invalid. Reply *1*, *2* or *3*.\n\n" +
                        LangPrompt();
            }

            Transition(s, "AWAITING_SHOP_CODE");
            return s.T(
                "✅ Language: *English*\n\n👉 Enter your *Shop Code*.\nExample: *20100090*",
                "✅ ভাষা: *বাংলা*\n\n👉 আপনার *শপ কোড* দিন।\nউদাহরণ: *20100090*",
                "✅ भाषा: *हिंदी*\n\n👉 अपना *शॉप कोड* दर्ज करें।\nउदाहरण: *20100090*");
        }

        // ─────────────────────────────────────────────────────────────────────
        // SHOP AUTHENTICATION
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleShopCodeAsync(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.MsgType != "text" || string.IsNullOrWhiteSpace(msg.RawText))
                return s.T(
                    "👉 Enter your *Shop Code*.\nExample: *20100090*",
                    "👉 আপনার *শপ কোড* দিন।\nউদাহরণ: *20100090*",
                    "👉 अपना *शॉप कोड* दर्ज करें।\nउदाहरण: *20100090*");

            var code = msg.RawText.Trim();
            var shop = await _spror.ValidateShopAsync(code);

            if (shop == null)
            {
                return s.T(
                    $"❌ *Shop Code not found.*\n\n*{code}* is not recognised.\n\n👉 Check and try again.\nExample: *20100090*",
                    $"❌ *শপ কোড পাওয়া যায়নি।*\n\n*{code}* সঠিক নয়।\n\n👉 আবার চেষ্টা করুন।\nউদাহরণ: *20100090*",
                    $"❌ *शॉप कोड नहीं मिला।*\n\n*{code}* सही नहीं।\n\n👉 पुनः प्रयास करें।\nउदाहरण: *20100090*");
            }

            s.ShopVerified = true;
            s.ShopCode = code;
            s.ShopUserId = shop.Id;
            s.ShopName = shop.ShopName;
            s.ShopCountryId = string.IsNullOrWhiteSpace(shop.ContId) ? "15" : shop.ContId;
            Transition(s, "MAIN_MENU");

            return s.T(
                $"✅ *Shop verified. Welcome back!*\n\n{BuildMainMenuBody("en")}",
                $"✅ *শপ যাচাই হয়েছে। স্বাগতম!*\n\n{BuildMainMenuBody("bn")}",
                $"✅ *दुकान सत्यापित। स्वागत है!*\n\n{BuildMainMenuBody("hi")}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // MAIN MENU
        // ─────────────────────────────────────────────────────────────────────

        private string BuildMainMenu(UaeSession s)
        {
            Transition(s, "MAIN_MENU");
            return BuildMainMenuBody(s.Lang ?? "en");
        }

        private static string BuildMainMenuBody(string lang) => lang switch
        {
            "bn" =>
                "🏪 *PRAN-RFL UAE Sales Support*\n\n" +
                "1️⃣  অর্ডার দিন\n" +
                "2️⃣  রিটার্ন / রিপ্লেসমেন্ট\n" +
                "3️⃣  অভিযোগ / ফিডব্যাক\n" +
                "4️⃣  সাপোর্ট এজেন্ট\n" +
                "5️⃣  সেলসম্যানের নম্বর\n" +
                "6️⃣  অর্ডার ট্র্যাক\n" +
                "0️⃣  ভাষা পরিবর্তন\n\n" +
                "👉 *1* থেকে *6* বা *0* পাঠান।",
            "hi" =>
                "🏪 *PRAN-RFL UAE Sales Support*\n\n" +
                "1️⃣  ऑर्डर करें\n" +
                "2️⃣  वापसी / प्रतिस्थापन\n" +
                "3️⃣  शिकायत / फ़ीडबैक\n" +
                "4️⃣  सपोर्ट एजेंट\n" +
                "5️⃣  सेल्समैन नंबर\n" +
                "6️⃣  ऑर्डर ट्रैक करें\n" +
                "0️⃣  भाषा बदलें\n\n" +
                "👉 *1* से *6* या *0* भेजें।",
            _ =>
                "🏪 *PRAN-RFL UAE Sales Support*\n\n" +
                "1️⃣  Place Order\n" +
                "2️⃣  Return / Replacement\n" +
                "3️⃣  Complaint / Feedback\n" +
                "4️⃣  Connect with Support Agent\n" +
                "5️⃣  Check Salesman Number\n" +
                "6️⃣  Track Order\n" +
                "0️⃣  Change Language\n\n" +
                "👉 Reply *1*, *2*, *3*, *4*, *5*, *6* or *0*.",
        };

        private async Task<string> HandleMainMenu(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.MsgType != "text") return BuildUnknown(s);
            // if/else required — switch expression cannot mix await with non-await arms
            if (msg.RawText == "1") return await StartOrderAsync(s);
            if (msg.RawText == "2") return StartReturn(s);
            if (msg.RawText == "3") return StartComplaint(s);
            if (msg.RawText == "4") return StartAgent(s);
            if (msg.RawText == "5") return await StartSalesmanAsync(s);
            if (msg.RawText == "6") return StartOrderTracking(s);
            if (msg.RawText == "0") return ResetToLang(s);
            return BuildUnknown(s);
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 1 — PLACE ORDER
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> StartOrderAsync(UaeSession s)
        {
            s.Cart.Clear();
            s.MenuMap.Clear();
            Transition(s, "AWAITING_CATEGORY");
            // Load categories immediately — do NOT return empty string here.
            // If we return empty, the user gets NO reply after selecting "1".
            return await HandleCategoryAsync(s, new UaeIncomingMessage { RawText = "" });
        }

        private async Task<string> HandleCategoryAsync(
            UaeSession s, UaeIncomingMessage msg)
        {
            // First entry — msg is the "1" from main menu, load categories
            if (!s.MenuMap.Any())
            {
                var cats = await _spror.GetCategoriesAsync();
                if (!cats.Any())
                    return s.T("⚠️ Categories unavailable. Please try again.",
                               "⚠️ ক্যাটাগরি পাওয়া যাচ্ছে না।",
                               "⚠️ श्रेणियाँ उपलब्ध नहीं।");

                s.MenuMap.Clear();
                for (int i = 0; i < cats.Count; i++)
                    s.MenuMap[(i + 1).ToString()] = $"cat:{cats[i].Id}|{cats[i].Name}";

                return BuildNumberedList(
                    s.T("🛒 *Place Order — Select Category*",
                        "🛒 *অর্ডার দিন — ক্যাটাগরি বেছে নিন*",
                        "🛒 *ऑर्डर करें — श्रेणी चुनें*"),
                    cats.Select(c => c.Name).ToList(),
                    s.T("0  Back to Main Menu", "0  মূল মেনু", "0  मुख्य मेनू"),
                    s.Cart);
            }

            // User selected a category number
            if (msg.RawText == "0") return BuildMainMenu(s);

            if (!s.MenuMap.TryGetValue(msg.RawText, out var catVal))
                return BuildUnknown(s);

            var parts = catVal.Split('|');
            s.SelectedCatId = parts[0].Replace("cat:", "");
            s.SelectedCatName = parts.Length > 1 ? parts[1] : "";
            s.MenuMap.Clear();
            Transition(s, "AWAITING_SUBCATEGORY");

            var subcats = await _spror.GetSubcategoriesAsync(s.SelectedCatId);
            if (!subcats.Any())
                return s.T("⚠️ No subcategories found.", "⚠️ সাবক্যাটাগরি নেই।", "⚠️ उपश्रेणी नहीं मिली।");

            for (int i = 0; i < subcats.Count; i++)
                s.MenuMap[(i + 1).ToString()] = $"subcat:{subcats[i].Id}|{subcats[i].Name}";

            return BuildNumberedList(
                s.T($"*{s.SelectedCatName}* — Select Subcategory",
                    $"*{s.SelectedCatName}* — সাবক্যাটাগরি বেছে নিন",
                    $"*{s.SelectedCatName}* — उपश्रेणी चुनें"),
                subcats.Select(c => c.Name).ToList(),
                s.T("0  Back to Categories", "0  ক্যাটাগরিতে ফিরুন", "0  श्रेणी में वापस"),
                s.Cart);
        }

        private async Task<string> HandleSubcategoryAsync(
            UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "0")
            {
                s.MenuMap.Clear();
                Transition(s, "AWAITING_CATEGORY");
                return await HandleCategoryAsync(s, msg);
            }

            if (!s.MenuMap.TryGetValue(msg.RawText, out var subcatVal))
                return BuildUnknown(s);

            var parts = subcatVal.Split('|');
            s.SelectedSubcatId = parts[0].Replace("subcat:", "");
            s.SelectedSubcatName = parts.Length > 1 ? parts[1] : "";
            s.MenuMap.Clear();

            var products = await _spror.GetProductsAsync(s.SelectedSubcatId);
            if (!products.Any())
                return s.T("⚠️ No products found.", "⚠️ পণ্য পাওয়া যায়নি।", "⚠️ उत्पाद नहीं मिले।");

            for (int i = 0; i < products.Count; i++)
                s.MenuMap[(i + 1).ToString()] = $"pid:{products[i].Id}";

            // Store full product data keyed by pid for when user selects
            // We store it in MenuMap with a prefix to retrieve later
            foreach (var p in products)
                // Name|Price|OldPrice|Code|ImageUrl|Factor|GroupId|PriceId
                s.MenuMap[$"__p:{p.Id}"] = FormattableString.Invariant(
                    $"{p.Name}|{p.Price}|{p.OldPrice}|{p.Code}|{p.ImageUrl}|{p.Factor}|{p.GroupId}|{p.PriceId}");

            Transition(s, "AWAITING_PRODUCT");

            var lines = products.Select(p =>
                $"{p.Name}  *{p.Price:F3} SAR*").ToList();

            return BuildNumberedList(
                s.T($"*{s.SelectedCatName} > {s.SelectedSubcatName}*\n\nEnter product number to select:",
                    $"*{s.SelectedCatName} > {s.SelectedSubcatName}*\n\nপণ্য নম্বর দিয়ে নির্বাচন করুন:",
                    $"*{s.SelectedCatName} > {s.SelectedSubcatName}*\n\nउत्पाद संख्या दर्ज करके चुनें:"),
                lines,
                s.T("0  Back to Subcategories", "0  সাবক্যাটাগরিতে ফিরুন", "0  उपश्रेणी में वापस"),
                s.Cart);
        }

        private string HandleProduct(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "0")
            {
                Transition(s, "AWAITING_SUBCATEGORY");
                return s.T("Please select a subcategory.",
                           "সাবক্যাটাগরি বেছে নিন।",
                           "उपश्रेणी चुनें।");
            }

            if (!s.MenuMap.TryGetValue(msg.RawText, out var pidVal) ||
                !pidVal.StartsWith("pid:"))
                return BuildUnknown(s);

            var pid = pidVal.Replace("pid:", "");
            if (!s.MenuMap.TryGetValue($"__p:{pid}", out var pData))
                return BuildUnknown(s);

            var parts = pData.Split('|');
            s.PendingCartPid = pid;
            s.PendingCartName = parts[0];
            s.PendingCartPrice = parts.Length > 1 && double.TryParse(parts[1],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pr) ? pr : 0;

            // Store full product metadata for order payload
            var item = new UaeCartItem
            {
                Pid = pid,
                Name = parts[0],
                Price = s.PendingCartPrice,
                OldPrice = parts.Length > 2 && double.TryParse(parts[2],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var op) ? op : 0,
                ProductCode = parts.Length > 3 ? parts[3] : "",
                ImageUrl = parts.Length > 4 ? parts[4] : "",
                Factor = parts.Length > 5 && int.TryParse(parts[5], out var f) ? f : 1,
                GroupId = parts.Length > 6 ? parts[6] : "",
                PriceId = parts.Length > 7 ? parts[7] : "",
                Qty = 0,
            };
            // Temporarily store in MenuMap as pending full item
            s.MenuMap["__pending_item"] = JsonSerializer.Serialize(item);

            Transition(s, "AWAITING_QTY");
            return s.T(
                $"*{s.PendingCartName}* — {s.PendingCartPrice:F3} SAR per unit\n\nHow many units?\n\n0  Back to Products\nS  Connect to Support Agent",
                $"*{s.PendingCartName}* — {s.PendingCartPrice:F3} SAR\n\nকত পিস লাগবে?\n\n0  পণ্যে ফিরুন\nS  এজেন্টের সাথে যোগাযোগ",
                $"*{s.PendingCartName}* — {s.PendingCartPrice:F3} SAR\n\nकितनी यूनिट चाहिए?\n\n0  उत्पाद में वापस\nS  एजेंट से जुड़ें");
        }

        private string HandleQty(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "0")
            {
                Transition(s, "AWAITING_PRODUCT");
                return s.T("Select a product.", "পণ্য বেছে নিন।", "उत्पाद चुनें।");
            }

            if (!int.TryParse(msg.RawText, out var qty) || qty <= 0)
                return s.T(
                    "❌ Please reply with a valid number (e.g. 1, 2, 5).",
                    "❌ সংখ্যা পাঠান (যেমন 1, 2, 5)।",
                    "❌ एक वैध संख्या भेजें (जैसे 1, 2, 5)।");

            // Find existing cart item or add new
            var pendingJson = s.MenuMap.GetValueOrDefault("__pending_item");
            UaeCartItem? newItem = null;
            if (!string.IsNullOrEmpty(pendingJson))
                try { newItem = JsonSerializer.Deserialize<UaeCartItem>(pendingJson); } catch { }

            if (newItem == null)
                newItem = new UaeCartItem
                {
                    Pid = s.PendingCartPid ?? "",
                    Name = s.PendingCartName ?? "",
                    Price = s.PendingCartPrice,
                    Qty = 0,
                };

            newItem.Qty = qty;
            s.MenuMap.Remove("__pending_item");

            var existing = s.Cart.FirstOrDefault(c => c.Pid == newItem.Pid);
            if (existing != null) existing.Qty += qty;
            else s.Cart.Add(newItem);

            Transition(s, "AWAITING_CART_ACTION");
            return BuildCartAddedMessage(s, newItem);
        }

        private async Task<string> HandleCartActionAsync(
            UaeSession s, UaeIncomingMessage msg)
        {
            return msg.RawText switch
            {
                "1" => await GoToCategories(s),
                "c" => BuildCartView(s),
                "x" => await CheckoutAsync(s),
                "0" => await GoToCategories(s),
                _ => BuildUnknown(s),
            };
        }

        private async Task<string> HandleCartViewAsync(
            UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "x") return await CheckoutAsync(s);
            if (msg.RawText == "1") return await GoToCategories(s);
            if (msg.RawText == "c") return ClearCartAction(s);

            // remove:N
            var removeMatch = Regex.Match(msg.RawText, @"^remove:(\d+)$");
            if (removeMatch.Success)
            {
                var idx = int.Parse(removeMatch.Groups[1].Value) - 1;
                if (idx >= 0 && idx < s.Cart.Count)
                {
                    s.Cart.RemoveAt(idx);
                    if (!s.Cart.Any()) return await StartOrderAsync(s);
                    return BuildCartView(s);
                }
            }

            return BuildCartView(s);
        }

        private async Task<string> CheckoutAsync(UaeSession s)
        {
            if (!s.Cart.Any())
                return s.T("🛒 Your cart is empty. Reply *1* to start shopping.",
                           "🛒 কার্ট খালি। *1* পাঠিয়ে শপিং শুরু করুন।",
                           "🛒 कार्ट खाली है। *1* भेजें।");

            var req = new PlaceOrderRequest
            {
                UserId = s.ShopUserId ?? "",
                CountryId = s.ShopCountryId ?? "15",
                GroupId = s.Cart.FirstOrDefault()?.GroupId ?? "",
                PriceId = s.Cart.FirstOrDefault()?.PriceId ?? "",
                TotalAmount = s.Cart.Sum(c => c.Amount),
                Items = s.Cart.Select(c => new UaeOrderItem
                {
                    Pid = c.Pid,
                    Name = c.Name,
                    Code = c.ProductCode,
                    ImageUrl = c.ImageUrl,
                    Price = c.Price,
                    OldPrice = c.OldPrice,
                    Qty = c.Qty,
                    Factor = c.Factor,
                    Amount = c.Amount,
                    GroupId = c.GroupId,
                    PriceId = c.PriceId,
                }).ToList(),
            };

            var result = await _spror.PlaceOrderAsync(req);
            if (!result.Success)
            {
                return s.T(
                    $"❌ Order failed.\n\n{result.Error}\n\nSend *X* to retry or *menu* for main menu.",
                    $"❌ অর্ডার ব্যর্থ।\n\n{result.Error}\n\n*X* পাঠিয়ে আবার চেষ্টা করুন।",
                    $"❌ ऑर्डर विफल।\n\n{result.Error}\n\n*X* भेजें पुनः प्रयास के लिए।");
            }

            var total = s.Cart.Sum(c => c.Amount);
            var itemLines = string.Join("\n",
                s.Cart.Select(c => $"  {c.Name} x{c.Qty} = {c.Amount:F3} SAR"));

            s.Cart.Clear();
            s.MenuMap.Clear();
            Transition(s, "MAIN_MENU");

            return s.T(
                $"✅ *Order Placed Successfully*\n\n" +
                $"Order ID : {result.OrderId}\n" +
                $"Total    : {total:F3} SAR\n\n" +
                $"Items:\n{itemLines}\n\n" +
                "Our team will contact you to confirm delivery.\n\n" +
                "*menu*  Back to Main Menu",

                $"✅ *অর্ডার সফল হয়েছে*\n\n" +
                $"অর্ডার আইডি : {result.OrderId}\n" +
                $"মোট : {total:F3} SAR\n\n" +
                $"পণ্যসমূহ:\n{itemLines}\n\n" +
                "আমাদের টিম শীঘ্রই আপনার সাথে যোগাযোগ করবে।\n\n" +
                "*menu*  মূল মেনু",

                $"✅ *ऑर्डर सफलतापूर्वक दिया*\n\n" +
                $"ऑर्डर ID : {result.OrderId}\n" +
                $"कुल : {total:F3} SAR\n\n" +
                $"आइटम:\n{itemLines}\n\n" +
                "हमारी टीम जल्द आपसे संपर्क करेगी।\n\n" +
                "*menu*  मुख्य मेनू");
        }

        private async Task<string> GoToCategories(UaeSession s)
        {
            s.MenuMap.Clear();
            Transition(s, "AWAITING_CATEGORY");
            return await HandleCategoryAsync(s, new UaeIncomingMessage { RawText = "" });
        }

        private string ClearCartAction(UaeSession s)
        {
            s.Cart.Clear();
            s.MenuMap.Clear();
            Transition(s, "AWAITING_CATEGORY");
            return s.T("🗑 Cart cleared. Select a category to start again.",
                       "🗑 কার্ট খালি হয়েছে।",
                       "🗑 कार्ट साफ़।");
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 2 — RETURN / REPLACEMENT
        // ─────────────────────────────────────────────────────────────────────

        private string StartReturn(UaeSession s)
        {
            ClearMedia(s);
            Transition(s, "AWAITING_RETURN_DETAILS");
            return s.T(
                "🔄 *Return / Replacement*\n\n" +
                "Tell us the product you want to return.\n\n" +
                "Send *Text*, *Image*, or *Voice*\n\n" +
                "👉 Send *0* to go back to main menu",

                "🔄 *রিটার্ন / রিপ্লেসমেন্ট*\n\n" +
                "যে পণ্যটি ফেরত দিতে চান তা জানান।\n\n" +
                "*টেক্সট*, *ছবি* বা *ভয়েস* পাঠান\n\n" +
                "👉 মূল মেনুতে ফিরতে *0* পাঠান",

                "🔄 *वापसी / प्रतिस्थापन*\n\n" +
                "जो उत्पाद वापस करना है उसके बारे में बताएं।\n\n" +
                "*टेक्स्ट*, *फ़ोटो* या *आवाज़* भेजें\n\n" +
                "👉 मुख्य मेनू पर जाने के लिए *0* भेजें");
        }

        private async Task<string> HandleReturnConfirmAsync(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "y") return await SubmitMediaAsync(s, "grv");
            if (msg.RawText == "n")
            {
                ClearMedia(s);
                return StartReturn(s);
            }
            // More details — treat as additional media
            Transition(s, "AWAITING_RETURN_DETAILS");
            return await HandleMediaDetailsAsync(s, msg, "return");
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 3 — COMPLAINT / FEEDBACK
        // ─────────────────────────────────────────────────────────────────────

        private string StartComplaint(UaeSession s)
        {
            ClearMedia(s);
            Transition(s, "AWAITING_COMPLAINT_DETAILS");
            return s.T(
                "📝 *Complaint / Feedback*\n\n" +
                "Tell us your problem.\n\n" +
                "Send *Text*, *Image*, or *Voice*\n\n" +
                "👉 Send *0* to go back to main menu",

                "📝 *অভিযোগ / ফিডব্যাক*\n\n" +
                "আপনার সমস্যা জানান।\n\n" +
                "*টেক্সট*, *ছবি* বা *ভয়েস* পাঠান\n\n" +
                "👉 মূল মেনুতে ফিরতে *0* পাঠান",

                "📝 *शिकायत / फ़ीडबैक*\n\n" +
                "अपनी समस्या बताएं।\n\n" +
                "*टेक्स्ट*, *फ़ोटो* या *आवाज़* भेजें\n\n" +
                "👉 मुख्य मेनू पर जाने के लिए *0* भेजें");
        }

        private async Task<string> HandleComplaintConfirmAsync(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "y") return await SubmitMediaAsync(s, "complaint");
            if (msg.RawText == "n")
            {
                ClearMedia(s);
                return StartComplaint(s);
            }
            Transition(s, "AWAITING_COMPLAINT_DETAILS");
            return await HandleMediaDetailsAsync(s, msg, "complaint");
        }

        // ─────────────────────────────────────────────────────────────────────
        // SHARED MEDIA HANDLER (Return + Complaint)
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleMediaDetailsAsync(
            UaeSession s, UaeIncomingMessage msg, string flowType)
        {
            var confirmState = flowType == "return"
                ? "AWAITING_RETURN_CONFIRM"
                : "AWAITING_COMPLAINT_CONFIRM";

            var alreadyInConfirm = s.State == confirmState;

            if (msg.MsgType == "text")
            {
                if (msg.RawText == "0") return BuildMainMenu(s);
                s.MediaDescription = string.IsNullOrWhiteSpace(s.MediaDescription)
                    ? msg.RawText
                    : s.MediaDescription + "\n" + msg.RawText;
            }
            else if (msg.MsgType == "image")
            {
                var imageId = await SaveMediaToDiskAsync(
                    msg.MessageId, msg.ImageId, msg.ImageMime,
                    msg.From, msg.SenderName, msg.Timestamp, "images",
                    caption: msg.ImageCaption);
                if (imageId != null) s.MediaImages.Add(imageId);

                // Burst suppression
                if (alreadyInConfirm)
                {
                    var now = msg.Timestamp > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(msg.Timestamp).UtcDateTime
                        : DateTime.UtcNow;
                    var isBurst = _state.LastImageTime.TryGetValue(s.Phone, out var last)
                        && Math.Abs((now - last).TotalSeconds) <= 3;
                    _state.LastImageTime[s.Phone] = now;
                    if (isBurst) return string.Empty;
                }
                else if (msg.MsgType == "image")
                {
                    _state.LastImageTime[s.Phone] = msg.Timestamp > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(msg.Timestamp).UtcDateTime
                        : DateTime.UtcNow;
                }
            }
            else if (msg.MsgType == "audio")
            {
                var voiceId = await SaveMediaToDiskAsync(
                    msg.MessageId, msg.AudioId, msg.AudioMime,
                    msg.From, msg.SenderName, msg.Timestamp, "audio");
                if (voiceId != null) s.MediaVoices.Add(voiceId);
            }
            else
            {
                return string.Empty; // unknown media type — silent ignore
            }

            Transition(s, confirmState);
            if (alreadyInConfirm && msg.MsgType == "image") return string.Empty;

            return s.T(
                "✅ *Received.*\n\n" +
                "Send *Y* to submit\n" +
                "Send *N* to cancel\n\n" +
                "To add more details, send another *Image*, *Voice* or *Text*",

                "✅ *পাওয়া গেছে।*\n\n" +
                "*Y* পাঠান জমা দিতে\n" +
                "*N* পাঠান বাতিল করতে\n\n" +
                "আরও যোগ করতে *ছবি*, *ভয়েস* বা *টেক্সট* পাঠান",

                "✅ *प्राप्त हुआ।*\n\n" +
                "जमा करने के लिए *Y* भेजें\n" +
                "रद्द करने के लिए *N* भेजें\n\n" +
                "अधिक जोड़ने के लिए *फ़ोटो*, *आवाज़* या *टेक्स्ट* भेजें");
        }

        private async Task<string> SubmitMediaAsync(UaeSession s, string ticketType)
        {
            var req = new UaeCrmRequest
            {
                ShopCode = s.ShopCode ?? "",
                WhatsappNumber = s.Phone,
                Description = s.MediaDescription,
                Images = new(s.MediaImages),
                VoiceFiles = new(s.MediaVoices),
                TicketType = ticketType,
            };

            var result = await _crm.SubmitAsync(req);
            ClearMedia(s);
            Transition(s, "MAIN_MENU");

            if (!result.Success)
                return s.T(
                    $"❌ Submission failed.\n{result.Error}\n\nSend *Y* to retry.",
                    $"❌ জমা ব্যর্থ।\n{result.Error}",
                    $"❌ जमा विफल।\n{result.Error}");

            var ticketLabel = ticketType == "grv"
                ? s.T("Return Request", "রিটার্ন রিকোয়েস্ট", "वापसी अनुरोध")
                : s.T("Complaint", "অভিযোগ", "शिकायत");

            return s.T(
                $"✅ *{ticketLabel} Submitted*\n\n" +
                (result.TicketId != null ? $"Ticket ID : *{result.TicketId}*\n\n" : "") +
                "Our team will contact you shortly.\n\n" +
                "👉 Send *menu* for Main Menu\n" +
                "👉 Send *S* to connect with Agent",

                $"✅ *{ticketLabel} জমা হয়েছে*\n\n" +
                (result.TicketId != null ? $"টিকেট আইডি : *{result.TicketId}*\n\n" : "") +
                "আমাদের টিম শীঘ্রই যোগাযোগ করবে।\n\n" +
                "👉 *menu* — মূল মেনু\n" +
                "👉 *S* — এজেন্টের সাথে যোগাযোগ",

                $"✅ *{ticketLabel} जमा हुआ*\n\n" +
                (result.TicketId != null ? $"टिकट ID : *{result.TicketId}*\n\n" : "") +
                "हमारी टीम जल्द संपर्क करेगी।\n\n" +
                "👉 *menu* — मुख्य मेनू\n" +
                "👉 *S* — एजेंट से जुड़ें");
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 4 — AGENT (double confirm)
        // ─────────────────────────────────────────────────────────────────────

        private string StartAgent(UaeSession s)
        {
            Transition(s, "AWAITING_AGENT_CONFIRM_1");
            return BuildAgentConfirm1(s);
        }

        private string BuildAgentConfirm1(UaeSession s) =>
            s.T(
                "📞 *Connect with Support Agent*\n\n" +
                "Our support agent will contact you after confirmation.\n\n" +
                "Send *Y* to Confirm\n" +
                "Send *N* to Cancel\n\n" +
                "👉 Send *0* to go back to main menu",

                "📞 *সাপোর্ট এজেন্ট*\n\n" +
                "নিশ্চিত করলে এজেন্ট আপনার সাথে যোগাযোগ করবে।\n\n" +
                "নিশ্চিত করতে *Y* পাঠান\n" +
                "বাতিল করতে *N* পাঠান\n\n" +
                "👉 মূল মেনুতে যেতে *0* পাঠান",

                "📞 *सपोर्ट एजेंट*\n\n" +
                "पुष्टि के बाद हमारा एजेंट आपसे संपर्क करेगा।\n\n" +
                "*Y* भेजें पुष्टि करने के लिए\n" +
                "*N* भेजें रद्द करने के लिए\n\n" +
                "👉 मुख्य मेनू पर जाने के लिए *0* भेजें");

        // Single-step: Y = connect immediately, N/0 = back
        private async Task<string> HandleAgentConfirm1Async(
            UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "y") return await ConnectAgentAsync(s);
            if (msg.RawText == "n" || msg.RawText == "0") return BuildMainMenu(s);
            return BuildAgentConfirm1(s); // re-show menu on invalid input
        }

        // Submits agent request to CRM with cart + category context
        private async Task<string> ConnectAgentAsync(UaeSession s)
        {
            // Build description from browsing context + cart
            var desc = new System.Text.StringBuilder("User requested live agent support.");

            if (!string.IsNullOrEmpty(s.SelectedCatName))
            {
                desc.Append($" Category: {s.SelectedCatName}");
                if (!string.IsNullOrEmpty(s.SelectedSubcatName))
                    desc.Append($" > {s.SelectedSubcatName}");
            }

            if (s.Cart.Any())
            {
                desc.Append(" | Cart: ");
                desc.Append(string.Join(", ",
                    s.Cart.Select(c => $"{c.Name} x{c.Qty} ({c.Amount:F3} SAR)")));
                desc.Append($" | Total: {s.Cart.Sum(c => c.Amount):F3} SAR");
            }

            var req = new UaeCrmRequest
            {
                ShopCode = s.ShopCode ?? "",
                WhatsappNumber = s.Phone,
                TicketType = "connect_to_agent",
                Description = desc.ToString(),
                CartItems = s.Cart.Any()
                    ? JsonSerializer.Serialize(s.Cart.Select(c => new
                    {
                        product_id = c.Pid,
                        product_name = c.Name,
                        product_code = c.ProductCode,
                        product_price = c.Price,
                        product_quantity = c.Qty,
                        amount = c.Amount,
                        factor = c.Factor,
                    }))
                    : "",
            };

            var result = await _crm.SubmitAsync(req);
            Transition(s, "MAIN_MENU");

            return result.Success
                ? s.T(
                    "✅ *Agent Request Submitted*\n\n" +
                    (result.TicketId != null ? $"Ticket ID : *{result.TicketId}*\n\n" : "") +
                    "A support agent will contact you shortly.\n\n" +
                    "👉 Send *menu* for Main Menu",

                    "✅ *অনুরোধ পাঠানো হয়েছে*\n\n" +
                    (result.TicketId != null ? $"টিকেট আইডি : *{result.TicketId}*\n\n" : "") +
                    "একজন এজেন্ট শীঘ্রই যোগাযোগ করবে।\n\n" +
                    "👉 *menu* — মূল মেনু",

                    "✅ *अनुरोध भेजा गया*\n\n" +
                    (result.TicketId != null ? $"टिकट ID : *{result.TicketId}*\n\n" : "") +
                    "एक एजेंट जल्द आपसे संपर्क करेगा।\n\n" +
                    "👉 *menu* — मुख्य मेनू")
                : s.T(
                    $"❌ Request failed.\n{result.Error}\n\nSend *S* to retry.",
                    $"❌ ব্যর্থ।\n{result.Error}",
                    $"❌ विफल।\n{result.Error}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 5 — SALESMAN
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> StartSalesmanAsync(UaeSession s)
        {
            Transition(s, "MAIN_MENU");

            if (string.IsNullOrEmpty(s.ShopCode))
                return s.T("❌ Shop not verified.", "❌ শপ যাচাই হয়নি।", "❌ दुकान सत्यापित नहीं।");

            var countryId = string.IsNullOrEmpty(s.ShopCountryId) ? "15" : s.ShopCountryId;
            var result = await _spror.GetSrAgentsAsync(s.ShopCode, countryId);

            if (!result.Success || !result.Agents.Any())
                return s.T(
                    "❌ No salesman found for your shop.\n\nPlease contact support.\n\n*menu*  Main Menu",
                    "❌ শপের জন্য সেলসম্যান পাওয়া যায়নি।\n\n*menu*  মূল মেনু",
                    "❌ दुकान के लिए सेल्समैन नहीं मिला।\n\n*menu*  मुख्य मेनू");

            var siteLine = !string.IsNullOrEmpty(result.SiteName)
                ? s.T("📋 *Salesman List — " + result.SiteName + "*",
                      "📋 *সেলসম্যান — " + result.SiteName + "*",
                      "📋 *सेल्समैन — " + result.SiteName + "*")
                : s.T("📋 *Your Salesman List*", "📋 *সেলসম্যান তালিকা*", "📋 *सेल्समैन सूची*");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(siteLine);
            sb.AppendLine();
            for (int i = 0; i < result.Agents.Count; i++)
                sb.AppendLine(string.Format("{0}. {1}  (ID: {2})",
                    i + 1, result.Agents[i].StaffName, result.Agents[i].StaffId));
            sb.AppendLine();
            sb.Append(s.T("*menu*  Back to Main Menu", "*menu*  মূল মেনু", "*menu*  मुख्य मेनू"));
            return sb.ToString();
        }
        private string HandleAreaInput(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "0") return BuildMainMenu(s);
            if (msg.MsgType != "text") return BuildUnknown(s);

            var salesman = SalesmanData.Find(msg.RawText);
            Transition(s, "MAIN_MENU");

            if (salesman == null)
                return s.T(
                    $"❌ No salesman found for *{msg.RawText}*.\n\nPlease try a nearby area.\n\n*menu*  Main Menu",
                    $"❌ *{msg.RawText}* এলাকায় সেলসম্যান পাওয়া যায়নি।\n\n*menu*  মূল মেনু",
                    $"❌ *{msg.RawText}* क्षेत्र में सेल्समैन नहीं मिला।\n\n*menu*  मुख्य मेनू");

            return s.T(
                $"👤 *Salesman — {msg.RawText}*\n\n{salesman}\n\n*menu*  Main Menu",
                $"👤 *সেলসম্যান — {msg.RawText}*\n\n{salesman}\n\n*menu*  মূল মেনু",
                $"👤 *सेल्समैन — {msg.RawText}*\n\n{salesman}\n\n*menu*  मुख्य मेनू");
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 6 — ORDER TRACKING
        // ─────────────────────────────────────────────────────────────────────

        private string StartOrderTracking(UaeSession s)
        {
            Transition(s, "AWAITING_ORDER_TRACKING");
            return s.T(
                "📦 *Track Order*\n\n👉 Send *1* to view your orders.\n\nSend *0* to go back.",
                "📦 *অর্ডার ট্র্যাক*\n\n👉 অর্ডার দেখতে *1* পাঠান।\n\n*0* পাঠান ফিরতে।",
                "📦 *ऑर्डर ट्रैक*\n\n👉 ऑर्डर देखने के लिए *1* भेजें।\n\n*0* भेजें वापस जाने के लिए।");
        }

        private async Task<string> HandleOrderTrackingAsync(
            UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "0") return BuildMainMenu(s);

            if (string.IsNullOrEmpty(s.ShopUserId))
                return s.T("Shop not verified.", "শপ যাচাই হয়নি।", "दुकान सत्यापित नहीं।");

            var json = await _spror.GetOrdersAsync(s.ShopUserId);
            Transition(s, "MAIN_MENU");

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Response: root → data (paginated object) → data (array of orders)
                if (!root.TryGetProperty("data", out var outerData) ||
                    outerData.ValueKind != JsonValueKind.Object)
                    return s.T("📦 No orders found.\n\n*menu*  Main Menu",
                               "📦 কোনো অর্ডার নেই।\n\n*menu*  মূল মেনু",
                               "📦 कोई ऑर्डर नहीं।\n\n*menu*  मुख्य मेनू");

                if (!outerData.TryGetProperty("data", out var dataEl) ||
                    dataEl.ValueKind != JsonValueKind.Array ||
                    dataEl.GetArrayLength() == 0)
                    return s.T("📦 No orders found.\n\n*menu*  Main Menu",
                               "📦 কোনো অর্ডার নেই।\n\n*menu*  মূল মেনু",
                               "📦 कोई ऑर्डर नहीं।\n\n*menu*  मुख्य मेनू");

                var lines = new List<string>();
                int i = 1;
                foreach (var order in dataEl.EnumerateArray())
                {
                    // Actual field names from API response
                    var oid = order.TryGetProperty("ordm_ornm", out var o) ? o.ToString() : "-";
                    var date = order.TryGetProperty("ordm_date", out var dt) ? dt.ToString() : "";
                    var status = order.TryGetProperty("status", out var st) ? st.ToString() : "-";
                    var total = order.TryGetProperty("ordm_amnt", out var ta) ? ta.ToString() : "-";

                    // Product summary from nested products array
                    var productLines = new List<string>();
                    if (order.TryGetProperty("products", out var prods) &&
                        prods.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in prods.EnumerateArray())
                        {
                            var pname = p.TryGetProperty("name", out var pn) ? pn.ToString() : "";
                            var qty = p.TryGetProperty("ordd_qnty", out var pq) ? pq.ToString() : "";
                            if (!string.IsNullOrEmpty(pname))
                                productLines.Add($"   • {pname} x{qty}");
                        }
                    }

                    var productSummary = productLines.Any()
                        ? "\n" + string.Join("\n", productLines)
                        : "";

                    lines.Add($"{i}. *{oid}*  {date}\n   {status}  |  {total} SAR{productSummary}");
                    i++;
                    if (i > 10) { lines.Add("..."); break; }
                }

                return s.T(
                    $"📦 *Your Orders*\n\n{string.Join("\n\n", lines)}\n\n*menu*  Main Menu",
                    $"📦 *আপনার অর্ডার*\n\n{string.Join("\n\n", lines)}\n\n*menu*  মূল মেনু",
                    $"📦 *आपके ऑर्डर*\n\n{string.Join("\n\n", lines)}\n\n*menu*  मुख्य मेनू");
            }
            catch
            {
                return s.T("⚠️ Could not load orders.\n\n*menu*  Main Menu",
                           "⚠️ অর্ডার লোড হয়নি।",
                           "⚠️ ऑर्डर लोड नहीं हुए।");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // WELCOME WITH LOGO
        // ─────────────────────────────────────────────────────────────────────

        private async Task SendWelcomeAsync(string phone, CancellationToken ct = default)
        {
            var baseUrl = _config["App:BaseUrl"]?.TrimEnd('/') ?? "https://webhook.prangroup.com";
            var logoUrl = $"{baseUrl}/images/pran-rfl-logo.jpg";
            var caption = LangPrompt();
            await _dialog.SendImageAsync(phone, logoUrl, caption, ct);
        }

        private static string LangPrompt() =>
            "👋 Hi! I'm *PRAN-RFL UAE Sales Support*\n\n" +
            "Please choose your language:\n\n" +
            "1️⃣  English\n" +
            "2️⃣  বাংলা\n" +
            "3️⃣  हिंदी\n\n" +
            "👉 Reply *1*, *2* or *3*.";

        // ─────────────────────────────────────────────────────────────────────
        // MEDIA SAVE
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string?> SaveMediaToDiskAsync(
            string messageId, string mediaId, string mimeType,
            string from, string senderName, long timestamp,
            string subFolder, string? caption = null)
        {
            try
            {
                var (bytes, mime) = await _dialog.DownloadMediaAsync(mediaId, mimeType);
                var ext = MimeToExt(mime, subFolder == "audio" ? ".ogg" : ".jpg");
                var fileName = $"{messageId}{ext}";
                var folder = Path.Combine(_env.WebRootPath, "wa-media", subFolder);
                Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(Path.Combine(folder, fileName), bytes);

                var baseUrl = _config["App:BaseUrl"] ?? "https://webhook.prangroup.com";
                var fileUrl = $"{baseUrl}/wa-media/{subFolder}/{fileName}";

                try
                {
                    await _msgRepo.InsertAsync(new WhatsAppMessage
                    {
                        MessageId = messageId,
                        FromNumber = from,
                        SenderName = senderName,
                        MessageType = subFolder == "audio" ? "audio" : "image",
                        MimeType = mime,
                        Caption = caption,
                        FileUrl = fileUrl,
                        FileSizeBytes = bytes.Length,
                        WaTimestamp = timestamp,
                        Status = "processed",
                        ProcessedAt = DateTime.UtcNow,
                    });
                }
                catch (Exception dbEx) when (
                    dbEx.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                    dbEx.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("[UAE] Media duplicate skipped: {Id}", messageId);
                }

                return messageId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UAE] SaveMedia failed msgId={Id}", messageId);
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // SESSION CACHE
        // ─────────────────────────────────────────────────────────────────────

        private async Task<UaeSession> LoadSessionAsync(string phone)
        {
            if (_cache.TryGetValue($"uae:{phone}", out UaeSession? cached) && cached != null)
                return cached;

            var row = await _sessionSvc.GetSessionAsync(phone);
            var session = UaeSession.Load(phone, row.TempData);
            if (session.State == "INIT" && row.CurrentStep != "INIT")
                session.State = row.CurrentStep;

            var opts = new MemoryCacheEntryOptions()
                .SetSlidingExpiration(TimeSpan.FromMinutes(60));
            _cache.Set($"uae:{phone}", session, opts);
            return session;
        }

        private async Task PersistSessionAsync(UaeSession s, string rawText)
        {
            var opts = new MemoryCacheEntryOptions()
                .SetSlidingExpiration(TimeSpan.FromMinutes(60));
            _cache.Set($"uae:{s.Phone}", s, opts);

            try
            {
                await _sessionSvc.UpsertSessionAsync(new UpsertSessionRequestDto
                {
                    Phone = s.Phone,
                    CurrentStep = s.State,
                    PreviousStep = s.PreviousState,
                    TempData = s.Save(),
                    RawMessage = rawText,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UAE] PersistSession failed {Phone}", s.Phone);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // HELPERS
        // ─────────────────────────────────────────────────────────────────────

        private static void Transition(UaeSession s, string newState)
        {
            s.PreviousState = s.State;
            s.State = newState;
        }

        private static void ClearMedia(UaeSession s)
        {
            s.MediaDescription = string.Empty;
            s.MediaImages = new();
            s.MediaVoices = new();
        }

        private static void ResetSession(UaeSession s)
        {
            s.State = "INIT";
            s.PreviousState = "INIT";
            s.Lang = null;
            s.Cart.Clear();
            s.MenuMap.Clear();
            ClearMedia(s);
        }

        private string ResetToLang(UaeSession s)
        {
            s.Lang = null;
            Transition(s, "AWAITING_LANG");
            return LangPrompt();
        }

        private string BuildUnknown(UaeSession s) =>
            s.T(
                "❌ *Invalid input.*\n\n👉 Send *menu* to go to Main Menu.",
                "❌ *অবৈধ ইনপুট।*\n\n👉 *menu* পাঠান।",
                "❌ *अमान्य इनपुट।*\n\n👉 *menu* भेजें।");

        private string BuildCartAddedMessage(UaeSession s, UaeCartItem item)
        {
            var total = s.Cart.Sum(c => c.Amount);
            var cartLines = s.Cart.Count == 1
                ? ""
                : "\n" + string.Join("\n", s.Cart.Select((c, i) =>
                    $"{i + 1}. {c.Name} x{c.Qty} = {c.Amount:F3} SAR"));

            return s.T(
                $"✅ *Added to Cart*\n\n{item.Name} x{item.Qty} = {item.Amount:F3} SAR" +
                $"{cartLines}\n\n💰 *Total: {total:F3} SAR*\n\n" +
                "1  Add More Products\nC  View Full Cart\nX  Checkout & Place Order\n0  Back to Categories\nS  Connect to Support Agent",

                $"✅ *কার্টে যোগ হয়েছে*\n\n{item.Name} x{item.Qty} = {item.Amount:F3} SAR" +
                $"{cartLines}\n\n💰 *মোট: {total:F3} SAR*\n\n" +
                "1  আরও পণ্য\nC  কার্ট দেখুন\nX  চেকআউট\n0  ক্যাটাগরিতে ফিরুন\nS  এজেন্টের সাথে যোগাযোগ",

                $"✅ *कार्ट में जोड़ा*\n\n{item.Name} x{item.Qty} = {item.Amount:F3} SAR" +
                $"{cartLines}\n\n💰 *कुल: {total:F3} SAR*\n\n" +
                "1  और उत्पाद\nC  कार्ट देखें\nX  चेकआउट\n0  श्रेणी में वापस\nS  एजेंट से जुड़ें");
        }

        private string BuildCartView(UaeSession s)
        {
            Transition(s, "AWAITING_CART_VIEW");

            if (!s.Cart.Any())
                return s.T("🛒 Your cart is empty.\n\n1  Start Shopping",
                           "🛒 কার্ট খালি।\n\n1  শপিং শুরু করুন",
                           "🛒 कार्ट खाली।\n\n1  शॉपिंग शुरू करें");

            var total = s.Cart.Sum(c => c.Amount);
            var lines = s.Cart.Select((c, i) =>
                $"{i + 1}. {c.Name} x{c.Qty} = {c.Amount:F3} SAR  _(remove:{i + 1})_");

            return s.T(
                $"🛒 *Your Cart*\n\n{string.Join("\n", lines)}\n\n💰 *Total: {total:F3} SAR*\n\n" +
                "X  Checkout & Place Order\n1  Add More Products\nC  Clear Cart\n0  Back\nS  Connect to Support Agent",

                $"🛒 *আপনার কার্ট*\n\n{string.Join("\n", lines)}\n\n💰 *মোট: {total:F3} SAR*\n\n" +
                "X  চেকআউট\n1  আরও পণ্য\nC  কার্ট খালি করুন\n0  ফিরুন\nS  এজেন্টের সাথে যোগাযোগ",

                $"🛒 *आपका कार्ट*\n\n{string.Join("\n", lines)}\n\n💰 *कुल: {total:F3} SAR*\n\n" +
                "X  चेकआउट\n1  और उत्पाद\nC  कार्ट साफ़ करें\n0  वापस\nS  एजेंट से जुड़ें");
        }

        private static string BuildNumberedList(
            string title, List<string> items, string backOption,
            List<UaeCartItem> cart)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(title);

            if (cart.Any())
            {
                sb.AppendLine($"\n🛒 Cart: {cart.Count} item(s) | {cart.Sum(c => c.Amount):F3} SAR");
            }

            sb.AppendLine();
            for (int i = 0; i < items.Count; i++)
                sb.AppendLine($"{i + 1}  {items[i]}");

            sb.AppendLine();
            sb.AppendLine(backOption);
            sb.AppendLine("S  Connect to Support Agent");
            return sb.ToString().TrimEnd();
        }

        private static string MimeToExt(string mime, string fallback) => mime switch
        {
            "audio/ogg" => ".ogg",
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "audio/opus" => ".opus",
            "audio/mp4" => ".m4a",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => fallback
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MESSAGE PARSER
    // ─────────────────────────────────────────────────────────────────────────

    public class UaeIncomingMessage
    {
        public string From { get; set; } = "";
        public string SenderName { get; set; } = "";
        public string MessageId { get; set; } = "";
        public string MsgType { get; set; } = "text";
        public long Timestamp { get; set; }
        public string RawText { get; set; } = "";
        public string AudioId { get; set; } = "";
        public string AudioMime { get; set; } = "audio/ogg";
        public string ImageId { get; set; } = "";
        public string ImageMime { get; set; } = "image/jpeg";
        public string ImageCaption { get; set; } = "";
    }

    public static class UaeMessageParser
    {
        public static UaeIncomingMessage? Parse(JsonElement body)
        {
            try
            {
                JsonElement? msgEl = null;
                string sender = string.Empty;

                if (body.TryGetProperty("entry", out var entries) &&
                    entries.GetArrayLength() > 0)
                {
                    var value = entries[0].GetProperty("changes")[0].GetProperty("value");
                    if (value.TryGetProperty("statuses", out _) &&
                        !value.TryGetProperty("messages", out _))
                        return null;
                    if (value.TryGetProperty("messages", out var msgs) &&
                        msgs.GetArrayLength() > 0)
                        msgEl = msgs[0];
                    if (value.TryGetProperty("contacts", out var contacts) &&
                        contacts.GetArrayLength() > 0 &&
                        contacts[0].TryGetProperty("profile", out var profile) &&
                        profile.TryGetProperty("name", out var nameEl))
                        sender = nameEl.GetString() ?? "";
                }
                else if (body.TryGetProperty("messages", out var directMsgs) &&
                         directMsgs.GetArrayLength() > 0)
                {
                    msgEl = directMsgs[0];
                    if (body.TryGetProperty("contacts", out var c) &&
                        c.GetArrayLength() > 0 &&
                        c[0].TryGetProperty("profile", out var p) &&
                        p.TryGetProperty("name", out var n))
                        sender = n.GetString() ?? "";
                }

                if (msgEl is null) return null;
                var msg = msgEl.Value;

                var from = S(msg, "from");
                var msgType = S(msg, "type");
                var msgId = S(msg, "id");
                var ts = long.TryParse(S(msg, "timestamp"), out var t) ? t : 0L;

                if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(msgType)) return null;

                string rawText = string.Empty;
                if (msgType == "text" &&
                    msg.TryGetProperty("text", out var textEl) &&
                    textEl.TryGetProperty("body", out var bodyEl))
                {
                    rawText = System.Text.RegularExpressions.Regex.Replace(
                        (bodyEl.GetString() ?? "").Trim().ToLowerInvariant(),
                        @"[\u200B-\u200D\uFEFF]", "");
                }

                string audioId = "", audioMime = "audio/ogg";
                if (msgType == "audio" && msg.TryGetProperty("audio", out var audio))
                {
                    audioId = S(audio, "id");
                    audioMime = S(audio, "mime_type") is { Length: > 0 } m ? m : "audio/ogg";
                }

                string imageId = "", imageMime = "image/jpeg", imageCap = "";
                if (msgType == "image" && msg.TryGetProperty("image", out var image))
                {
                    imageId = S(image, "id");
                    imageMime = S(image, "mime_type") is { Length: > 0 } m ? m : "image/jpeg";
                    imageCap = S(image, "caption");
                }

                return new UaeIncomingMessage
                {
                    From = from,
                    SenderName = sender,
                    MessageId = msgId,
                    MsgType = msgType,
                    Timestamp = ts,
                    RawText = rawText,
                    AudioId = audioId,
                    AudioMime = audioMime,
                    ImageId = imageId,
                    ImageMime = imageMime,
                    ImageCaption = imageCap,
                };
            }
            catch { return null; }
        }

        private static string S(JsonElement el, string key) =>
            el.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
    }
}
