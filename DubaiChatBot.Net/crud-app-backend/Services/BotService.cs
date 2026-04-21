using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using crud_app_backend.Bot.Models;
using crud_app_backend.DTOs;
using crud_app_backend.Models;
using crud_app_backend.Repositories;
using crud_app_backend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace crud_app_backend.Bot.Services
{
    /// <summary>
    /// Complete WhatsApp chatbot — exact replica of n8n workflow.
    /// Every message string matches the n8n Code node character-for-character.
    /// Includes: Fix1 (IMemoryCache session), Fix4 (no ExistsAsync on media).
    /// Perf: Instant ACK on external-HTTP paths, sliding session cache (60 min).
    /// </summary>
    public class BotService : IBotService
    {
        private readonly IWhatsAppSessionService _sessionSvc;
        private readonly IWhatsAppComplaintService _complaintSvc;
        private readonly IWhatsAppMessageRepository _msgRepo;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly IDialogClient _dialog;
        private readonly IHrisService _hris;
        private readonly IHttpClientFactory _httpFactory;
        private readonly IMemoryCache _cache;
        private readonly ILogger<BotService> _logger;
        private readonly BotStateService _state; // singleton — shared across all requests

        public BotService(
            IWhatsAppSessionService sessionSvc,
            IWhatsAppComplaintService complaintSvc,
            IWhatsAppMessageRepository msgRepo,
            IWebHostEnvironment env,
            IConfiguration config,
            IDialogClient dialog,
            IHrisService hris,
            IHttpClientFactory httpFactory,
            IMemoryCache cache,
            ILogger<BotService> logger,
            BotStateService state)
        {
            _sessionSvc = sessionSvc;
            _complaintSvc = complaintSvc;
            _msgRepo = msgRepo;
            _env = env;
            _config = config;
            _dialog = dialog;
            _hris = hris;
            _httpFactory = httpFactory;
            _cache = cache;
            _logger = logger;
            _state = state;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ENTRY POINT
        // Replaces: Webhook → Parse Message → GET Session → Merge Session
        //           → Auth Router → Is Verified → (route)
        // ─────────────────────────────────────────────────────────────────────

        public async Task ProcessAsync(JsonElement body)
        {
            try
            {
                var msg = MessageParser.Parse(body);
                if (msg is null) return;

                _logger.LogInformation("[Bot] {Type} from {Phone} id={Id}",
                    msg.MsgType, msg.From, msg.MessageId);

                // Per-user lock: if the same user sends 3 images simultaneously
                // (gallery burst), queue them so they process one at a time.
                // Users never block each other — each phone has its own semaphore.
                var userLock = _state.UserLocks.GetOrAdd(msg.From, _ => new SemaphoreSlim(1, 1));
                await userLock.WaitAsync();
                try
                {
                    var session = await LoadSessionAsync(msg.From);

                    // PERF: Send an instant acknowledgement for paths that will call
                    // an external HTTP API (HRIS verify, CRM submit, CRM lookup).
                    var ack = GetAckMessage(session, msg);
                    if (ack != null)
                        await _dialog.SendTextAsync(msg.From, ack);

                    var reply = await RouteAsync(session, msg);

                    // ALWAYS persist session — even when reply is empty (burst image
                    // suppression). Without this, burst image IDs added to
                    // s.ComplaintImages are never saved and disappear from the complaint.
                    if (string.IsNullOrWhiteSpace(reply))
                    {
                        await PersistSessionAsync(session, msg.RawText);
                        return;
                    }

                    // Persist session to DB and send the final reply in parallel
                    await Task.WhenAll(
                        PersistSessionAsync(session, msg.RawText),
                        _dialog.SendTextAsync(msg.From, reply)
                    );
                }
                finally
                {
                    userLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Bot] ProcessAsync unhandled crash");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // INSTANT ACK
        // Returns a short acknowledgement string for the 4 paths that hit
        // external HTTP (HRIS verify, CRM lookup, CRM submit × 2).
        // Returns null for all other paths — no ack needed.
        // ─────────────────────────────────────────────────────────────────────

        private static string? GetAckMessage(BotSession s, IncomingMessage msg)
        {
            // HRIS staff ID verification
            if (!s.StaffVerified &&
                s.State == "AWAITING_STAFF_ID" &&
                msg.MsgType == "text" &&
                !string.IsNullOrWhiteSpace(msg.RawText))
                return s.Lang == "bn"
                    ? "🔍 যাচাই করা হচ্ছে..."
                    : "🔍 Verifying...";

            // CRM complaint/ticket status lookup
            if (s.State == "AWAITING_COMPLAINT_ID" && msg.MsgType == "text")
                return s.Lang == "bn"
                    ? "🔍 খোঁজা হচ্ছে..."
                    : "🔍 Looking up your complaint...";

            // CRM complaint submission
            if (s.State == "AWAITING_COMPLAINT_CONFIRM" && msg.RawText == "y")
                return s.Lang == "bn"
                    ? "⏳ সাবমিট দেওয়া হচ্ছে..."
                    : "⏳ Submitting your complaint...";

            // CRM agent connect request
            if (s.State == "AWAITING_AGENT_CONFIRM" && msg.RawText == "y")
                return s.Lang == "bn"
                    ? "⏳ অনুরোধ পাঠানো হচ্ছে..."
                    : "⏳ Sending your request...";

            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ROUTER
        // Replaces: Auth Router → Is Verified → Auth Handler → Skip Auth IF
        //           → Resolve Menu Choice → State Router
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> RouteAsync(BotSession s, IncomingMessage msg)
        {
            // Is Verified = false → Auth Handler
            if (!s.StaffVerified)
                return await HandleUnverifiedAsync(s, msg);

            // Is Verified = true → Resolve Menu Choice
            // Global: menu / main menu / 0 (text only) → Build Main Menu
            if (msg.MsgType == "text" &&
                (msg.RawText == "menu" || msg.RawText == "main menu" || msg.RawText == "0"))
                return BuildMainMenu(s);

            // State Router
            return s.State switch
            {
                "MAIN_MENU" => RouteMainMenu(s, msg),
                "AWAITING_COMPLAINT_DETAIL" => await HandleComplaintDetailAsync(s, msg),
                "AWAITING_COMPLAINT_CONFIRM" => await HandleComplaintConfirmAsync(s, msg),
                "AWAITING_COMPLAINT_ID" => await HandleStatusCheckAsync(s, msg),
                "AWAITING_AGENT_CONFIRM" => await HandleAgentConfirmAsync(s, msg),
                _ => HandleUnknownVerified(s),
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // AUTH HANDLER  (unverified: INIT / AWAITING_LANG / AWAITING_STAFF_ID)
        // Matches Auth Handler node + Check Staff Valid + Staff Verified Welcome
        //         + Staff Not Found + Ask Staff ID Prompt
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleUnverifiedAsync(BotSession s, IncomingMessage msg)
        {
            // INIT → language selection  (Auth Handler INIT block)
            if (s.State == "INIT")
            {
                Transition(s, "AWAITING_LANG");
                // Send logo image + welcome text as caption.
                // If no logo media ID is configured, SendImageAsync falls back to plain text.
                await SendWelcomeAsync(msg.From);
                return string.Empty; // reply already sent via SendWelcomeAsync
            }

            // AWAITING_LANG → language selection  (Auth Handler AWAITING_LANG block)
            if (s.State == "AWAITING_LANG")
            {
                var input = msg.RawText.Trim();
                if (input == "1" || input == "2")
                {
                    s.Lang = input == "1" ? "en" : "bn";
                    Transition(s, "AWAITING_STAFF_ID");
                    return s.T(
                        "Please provide your HRIS Staff ID for verification.\n\nExample: *123456*",
                        "যাচাইয়ের জন্য আপনার HRIS স্টাফ আইডি দিন।\n\nউদাহরণ: *123456*");
                }
                // Invalid — re-ask  (Auth Handler invalid lang block)
                return
                    "❌ Invalid input. Please reply with *1* or *2*.\n" +
                    "❌ ভুল ইনপুট। *1* অথবা *2* পাঠান।\n\n" +
                    "1️⃣ English\n" +
                    "2️⃣ বাংলা";
            }

            // AWAITING_STAFF_ID
            if (s.State == "AWAITING_STAFF_ID")
            {
                // State Router route[16] INVALID_STAFF_INPUT: non-text → Ask Staff ID Prompt
                if (msg.MsgType != "text" || string.IsNullOrWhiteSpace(msg.RawText))
                    return s.T(
                        "Please provide your HRIS Staff ID for verification.\n\nExample: 552188",
                        "যাচাইয়ের জন্য আপনার HRIS স্টাফ আইডি দিন।\n\nউদাহরণ: 552188");

                var staffId = msg.RawText.Trim();
                var staff = await _hris.VerifyAsync(staffId);

                // Check Staff Valid [0]: SUCCESS_CODE=2000 AND STATUS=Active → Staff Verified Welcome
                if (staff != null && string.Equals(staff.Status, "Active",
                    StringComparison.OrdinalIgnoreCase))
                {
                    s.StaffVerified = true;
                    s.StaffId = staff.Id;
                    s.StaffName = staff.Name;
                    s.StaffContact = staff.ContactNo;
                    s.StaffEmail = staff.Email;
                    s.StaffDesignation = staff.Designation;
                    s.StaffDept = staff.Department;
                    s.StaffGroup = staff.GroupName;
                    s.StaffCompany = staff.Company;
                    s.StaffLocation = staff.LocationName;
                    Transition(s, "MAIN_MENU");

                    return s.T(
                        $"Hi, {staff.Name}!\n" +
                        "What would you like to do?\n\n" +
                        "1️⃣ Submit Complaint\n" +
                        "2️⃣ Check Complaint Status\n" +
                        "3️⃣ Connect with Support Agent\n\n" +
                        "Reply with 1, 2, or 3.",

                        $"হ্যালো, {staff.Name}!\n" +
                        "আপনি কী করতে চান?\n\n" +
                        "1️⃣ অভিযোগ সাবমিট দিন\n" +
                        "2️⃣ অভিযোগের স্ট্যাটাস দেখুন\n" +
                        "3️⃣ সাপোর্ট এজেন্টের সাথে কথা বলুন\n\n" +
                        "1, 2 অথবা 3 পাঠান।");
                }

                // Check Staff Valid [1] → Staff Not Found
                var isInactive = staff != null &&
                    !string.Equals(staff.Status, "Active", StringComparison.OrdinalIgnoreCase);
                Transition(s, "AWAITING_STAFF_ID");

                if (isInactive)
                    return s.T(
                        $"❌ *Invalid Staff ID*\n\nYour account is *{staff!.Status}*.\nPlease check and try again.\n\nExample:123456 ",
                        $"❌ *ভুল স্টাফ আইডি*\n\nআপনার অ্যাকাউন্ট *{staff!.Status}*।\nঅনুগ্রহ করে আবার চেষ্টা করুন।\n\nউদাহরণ: 123456");

                return s.T(
                    $"❌ *Invalid Staff ID*\n\nThe Staff ID *{staffId}* was not found.\nPlease check and try again.\n\nExample: 123456",
                    $"❌ *ভুল স্টাফ আইডি*\n\nস্টাফ আইডি *{staffId}* পাওয়া যায়নি।\nঅনুগ্রহ করে আবার চেষ্টা করুন।\n\nউদাহরণ: 123456");
            }

            // Fallback: reset to INIT
            Transition(s, "INIT");
            await SendWelcomeAsync(msg.From);
            return string.Empty; // reply already sent via SendWelcomeAsync
        }

        // ─────────────────────────────────────────────────────────────────────
        // MAIN MENU ROUTER  (Resolve Menu Choice + State Router for MAIN_MENU)
        // ─────────────────────────────────────────────────────────────────────

        private string RouteMainMenu(BotSession s, IncomingMessage msg)
        {
            return msg.RawText switch
            {
                "1" => StartComplaint(s),    // START_COMPLAINT
                "2" => StartStatusCheck(s),  // CHECK_STATUS
                "3" => StartAgentConnect(s), // CONNECT_AGENT
                _ => HandleUnknownMainMenu(s) // INVALID_CHOICE / UNKNOWN
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // HANDLE UNKNOWN — context-aware  (matches Handle Unknown node exactly)
        // ─────────────────────────────────────────────────────────────────────

        private string HandleUnknownMainMenu(BotSession s)
        {
            var lang = s.Lang ?? "en";
            return lang == "bn"
                ? "❓ *ভুল ইনপুট।*\n\nঅনুগ্রহ করে পাঠান:\n1️⃣ অভিযোগ সাবমিট দিন\n2️⃣ অভিযোগের স্ট্যাটাস\n3️⃣ সাপোর্ট এজেন্ট\n\nঅথবা *menu* টাইপ করুন।"
                : "❓ *Invalid input.*\n\nPlease reply with:\n1️⃣ Submit Complaint\n2️⃣ Check Complaint Status\n3️⃣ Connect with Support Agent\n\nReply 1, 2, 3 to return.";
        }

        private string HandleUnknownVerified(BotSession s)
        {
            // Handle Unknown — else branch
            return s.T(
                "❓ *Invalid input.*\n\nType *menu* to return to main menu.",
                "❓ *ভুল ইনপুট।*\n\nমূল মেনুতে ফিরতে *menu* টাইপ করুন।");
        }

        // ─────────────────────────────────────────────────────────────────────
        // COMPLAINT DETAIL
        // Replaces: Complaint Media Switch → Store Text/Voice/Image
        //           → Show Complaint Confirm
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleComplaintDetailAsync(BotSession s, IncomingMessage msg)
        {
            // Track if we were already in confirm state BEFORE processing this message.
            // Used below to suppress duplicate confirm screens on gallery burst sends.
            var alreadyInConfirm = s.State == "AWAITING_COMPLAINT_CONFIRM";

            switch (msg.MsgType)
            {
                case "text":
                    // Store Complaint Text
                    var prev = s.ComplaintDescription;
                    s.ComplaintDescription = string.IsNullOrWhiteSpace(prev)
                        ? msg.RawText : prev + "\n" + msg.RawText;
                    break;

                case "audio":
                    // Get Audio Media URL → Download Audio Binary → POST Voice to Backend → Store Complaint Voice
                    var voiceId = await SaveMediaToDiskAsync(
                        msg.MessageId, msg.AudioId, msg.AudioMime,
                        msg.From, msg.SenderName, msg.Timestamp, "audio");
                    if (voiceId != null)
                        s.ComplaintVoices.Add(voiceId);
                    else
                        return s.T(
                            "⚠️ Voice note could not be saved. Please try again.",
                            "⚠️ ভয়েস নোট সংরক্ষণ করা যায়নি। আবার চেষ্টা করুন।");
                    break;

                case "image":
                    // Get Image Media URL → Download Image Binary → POST Image to Backend → Store Complaint Image
                    var imageId = await SaveMediaToDiskAsync(
                        msg.MessageId, msg.ImageId, msg.ImageMime,
                        msg.From, msg.SenderName, msg.Timestamp, "images",
                        caption: msg.ImageCaption);
                    if (imageId != null)
                        s.ComplaintImages.Add(imageId);
                    else
                        return s.T(
                            "⚠️ Image could not be saved. Please try again.",
                            "⚠️ ছবি সংরক্ষণ করা যায়নি। আবার চেষ্টা করুন।");
                    break;

                default:
                    // BUG FIX: Unknown media type (document, sticker, video etc.)
                    // Some Android WhatsApp versions send gallery images as "document".
                    // Silently ignore — no confusing error shown. Session persisted by caller.
                    return string.Empty;
            }

            // Always transition to confirm state
            Transition(s, "AWAITING_COMPLAINT_CONFIRM");

            // Burst vs one-by-one detection:
            // Gallery burst  → all images have the SAME WhatsApp timestamp (sent together)
            // One by one     → WA timestamps differ by several seconds
            //
            // IMPORTANT: we use msg.Timestamp (WhatsApp's own Unix timestamp, seconds)
            // NOT DateTime.UtcNow. Using UtcNow was the bug — because images queue
            // behind the SemaphoreSlim, Image 3 only starts processing after Images 1+2
            // finish (each taking 1-3s for media download). By that point UtcNow-based
            // gap exceeds 2s and Image 3 is no longer detected as a burst.
            // WA timestamp is set by WhatsApp at send time — all gallery images share
            // the same value regardless of how long our processing takes.
            if (msg.MsgType == "image")
            {
                // Convert WA unix timestamp → DateTime. Fall back to UtcNow if missing.
                var waTime = msg.Timestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(msg.Timestamp).UtcDateTime
                    : DateTime.UtcNow;

                if (alreadyInConfirm)
                {
                    var isBurst = _state.LastImageTime.TryGetValue(s.Phone, out var lastTime)
                        && Math.Abs((waTime - lastTime).TotalSeconds) <= 3;

                    _state.LastImageTime[s.Phone] = waTime;

                    if (isBurst)
                    {
                        _logger.LogDebug("[Bot] Gallery burst suppressed for {Phone}", s.Phone);
                        return string.Empty; // image saved silently — no duplicate confirm
                    }

                    // One-by-one (gap > 3s): fall through → show confirm so user knows image received
                }
                else
                {
                    // First image in this complaint — record WA timestamp
                    _state.LastImageTime[s.Phone] = waTime;
                }
            }

            return BuildConfirmScreen(s);
        }

        // ─────────────────────────────────────────────────────────────────────
        // COMPLAINT CONFIRM
        // Resolve Menu Choice: Y=CONFIRM, N=CANCEL, else=SUBMIT_COMPLAINT_INPUT
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleComplaintConfirmAsync(BotSession s, IncomingMessage msg)
        {
            if (msg.RawText == "y") return await SubmitAsync(s, "complaint");
            if (msg.RawText == "n") return CancelComplaint(s);

            // BUG FIX: do NOT transition state before calling HandleComplaintDetailAsync.
            // If we transition first, alreadyInConfirm reads the new state (AWAITING_COMPLAINT_DETAIL)
            // and is always false — burst detection never fires — causing 3 confirm screens
            // for 3 gallery images. By skipping the transition here, alreadyInConfirm correctly
            // sees AWAITING_COMPLAINT_CONFIRM and burst suppression works properly.
            return await HandleComplaintDetailAsync(s, msg);
        }

        // ─────────────────────────────────────────────────────────────────────
        // STATUS CHECK
        // Replaces: Lookup Complaint API1 → Handle Lookup Result
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleStatusCheckAsync(BotSession s, IncomingMessage msg)
        {
            var ticketId = msg.RawText.Trim();
            _logger.LogInformation("[Bot] Lookup ticketId={Id} staffId={Staff}", ticketId, s.StaffId);

            string? respRaw = null;
            bool httpOk = false;

            try
            {
                var client = _httpFactory.CreateClient("CrmClient");
                var crmBase = _config["Crm:LookupUrl"]
                              ?? "https://crm.prangroup.com/api/whats-app/sales-support/v1/details";

                _logger.LogInformation("[Bot] CRM GET {Url}", $"{crmBase}/{ticketId}");
                var resp = await client.GetAsync($"{crmBase}/{ticketId}");
                respRaw = await resp.Content.ReadAsStringAsync();
                httpOk = resp.IsSuccessStatusCode;
                _logger.LogInformation("[Bot] CRM {Code} body={B}",
                    (int)resp.StatusCode, respRaw.Length > 200 ? respRaw[..200] : respRaw);
            }
            catch (TaskCanceledException)
            {
                // CRM timed out (HttpClient timeout = 60s after our Program.cs change)
                _logger.LogWarning("[Bot] CRM lookup timed out for ticketId={Id}", ticketId);
                return s.T(
                    "⏱ Support system is taking too long to respond.\n\nPlease try again in a moment.\n\nSend *0* to go back.",
                    "⏱ সিস্টেম সাড়া দিতে দেরি হচ্ছে।\n\nঅনুগ্রহ করে একটু পরে আবার চেষ্টা করুন।\n\nফিরতে *0* পাঠান।");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Bot] CRM lookup crashed ticketId={Id}", ticketId);
                return s.T(
                    $"Could not reach support system ({ex.GetType().Name}).\n\nSend *0* to go back.",
                    $"সিস্টেমে পৌঁছানো যাচ্ছে না।\n\nফিরতে *0* পাঠান।");
            }

            if (!httpOk || string.IsNullOrEmpty(respRaw))
            {
                Transition(s, "AWAITING_COMPLAINT_ID");
                return s.T(
                    $"❌ Complaint not found.\n\nTicket ID *{ticketId}* was not found.\nPlease check the ID and try again.\n\nSend *0* to go back to main menu.",
                    $"❌ অভিযোগ পাওয়া যায়নি।\n\nটিকেট আইডি *{ticketId}* পাওয়া যায়নি।\nঅনুগ্রহ করে আইডি চেক করে আবার চেষ্টা করুন।\n\nমূল মেনুতে ফিরতে *0* পাঠান।");
            }

            // Parse response — Handle Lookup Result node
            Dictionary<string, JsonElement>? parsed;
            Dictionary<string, JsonElement>? data = null;
            try
            {
                parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(respRaw);
                if (parsed != null &&
                    parsed.TryGetValue("status", out var sv) && sv.GetString() == "success" &&
                    parsed.TryGetValue("data", out var dataEl))
                    data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dataEl.GetRawText());
            }
            catch { parsed = null; }

            if (data == null)
            {
                Transition(s, "AWAITING_COMPLAINT_ID");
                return s.T(
                    $"❌ Complaint not found.\n\nTicket ID *{ticketId}* was not found.\nPlease check the ID and try again.\n\nSend *0* to go back to main menu.",
                    $"❌ অভিযোগ পাওয়া যায়নি।\n\nটিকেট আইডি *{ticketId}* পাওয়া যায়নি।\nঅনুগ্রহ করে আইডি চেক করে আবার চেষ্টা করুন।\n\nমূল মেনুতে ফিরতে *0* পাঠান।");
            }

            string G(string k) => data.TryGetValue(k, out var v) &&
                v.ValueKind != JsonValueKind.Null ? v.ToString() : "";

            // Staff ID ownership check
            var ticketStaffId = G("staff_id").Trim();
            var sessionStaffId = (s.StaffId ?? "").Trim();
            if (!string.IsNullOrEmpty(ticketStaffId) &&
                !string.IsNullOrEmpty(sessionStaffId) &&
                ticketStaffId != sessionStaffId)
            {
                Transition(s, "AWAITING_COMPLAINT_ID");
                return s.T(
                    $"❌ This ticket does not belong to you.\n\nTicket ID *{ticketId}* belongs to another staff member.\nPlease enter your own ticket ID.\n\nSend *0* to go back to main menu.",
                    $"❌ এই টিকেটটি আপনার নয়।\n\nটিকেট আইডি *{ticketId}* অন্য স্টাফের।\nঅনুগ্রহ করে আপনার নিজের টিকেট আইডি দিন।\n\nমূল মেনুতে ফিরতে *0* পাঠান।");
            }

            // Show ticket status only
            var foundTicketId = G("id");
            var createdRaw = G("created_at");
            var ticketStatus = G("status");

            var createdFmt = string.Empty;
            if (!string.IsNullOrEmpty(createdRaw) && DateTime.TryParse(createdRaw, out var dtUtc))
            {
                try
                {
                    var dhaka = TimeZoneInfo.FindSystemTimeZoneById(
                        OperatingSystem.IsWindows() ? "Bangladesh Standard Time" : "Asia/Dhaka");
                    var dhakaTime = TimeZoneInfo.ConvertTimeFromUtc(dtUtc.ToUniversalTime(), dhaka);
                    createdFmt = dhakaTime.ToString("d MMM yyyy, HH:mm",
                        System.Globalization.CultureInfo.InvariantCulture);
                }
                catch { createdFmt = dtUtc.ToString("d MMM yyyy, HH:mm"); }
            }

            var statusDisplay = !string.IsNullOrWhiteSpace(ticketStatus)
                ? ticketStatus
                : (s.Lang == "bn" ? "প্রক্রিয়াধীন" : "In Progress");

            var L = s.Lang == "bn"
                ? (title: "টিকেট স্ট্যাটাস", id: "টিকেট আইডি",
                   status: "স্ট্যাটাস", date: "সাবমিট দেওয়া হয়েছে",
                   back: "মূল মেনুতে ফিরতে *0* পাঠান।")
                : (title: "Ticket Status", id: "Ticket ID",
                   status: "Status", date: "Submitted",
                   back: "Send *0* to go back to main menu.");

            Transition(s, "MAIN_MENU");
            return
                $"{L.title}\n\n" +
                $"{L.id}: {foundTicketId}\n" +
                $"{L.status}: {statusDisplay}\n" +
                (string.IsNullOrEmpty(createdFmt) ? "" : $"{L.date}: {createdFmt}\n") +
                $"\n{L.back}";
        }

        // ─────────────────────────────────────────────────────────────────────
        // AGENT CONFIRM
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleAgentConfirmAsync(BotSession s, IncomingMessage msg)
        {
            if (msg.RawText == "y") return await SubmitAsync(s, "agent_connect");
            if (msg.RawText == "n") return CancelAgent(s);
            // Invalid Agent Confirm node
            return s.T(
                "❌ Invalid input.\n\nPlease send: *Y* for Confirm,  *N* for Cancel, *0* to got Main Menu",
                "❌ ভুল ইনপুট।\n\nঅনুগ্রহ করে নিশ্চিত করতে *Y*, বাতিল করতে *Y*, মূল মেনু যেতে *0* পাঠান");
        }

        // ─────────────────────────────────────────────────────────────────────
        // COMPLAINT SUBMISSION
        // Replaces: Submit Complaint API / Submit Agent API
        //           → Handle Complaint Result / Handle Agent Result
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> SubmitAsync(BotSession s, string complaintType)
        {
            var req = new SubmitComplaintRequestDto
            {
                WhatsappPhone = s.Phone,
                StaffId = s.StaffId ?? string.Empty,
                Name = s.StaffName,
                OfficialPhone = s.StaffContact,
                Designation = s.StaffDesignation,
                Dept = s.StaffDept,
                GroupName = s.StaffGroup,
                Company = s.StaffCompany,
                LocationName = s.StaffLocation,
                Email = s.StaffEmail,
                Description = complaintType == "agent_connect"
                    ? "User wants to connect with a support agent"
                    : (string.IsNullOrWhiteSpace(s.ComplaintDescription) ? " " : s.ComplaintDescription),
                ComplaintType = complaintType,
                VoiceMessageIds = complaintType == "agent_connect" ? new() : new(s.ComplaintVoices),
                ImageMessageIds = complaintType == "agent_connect" ? new() : new(s.ComplaintImages),
            };

            var result = await _complaintSvc.SubmitAsync(req, CancellationToken.None);

            if (!result.Success)
            {
                var errMsg = result.ErrorMessage ?? result.Message
                    ?? s.T("Could not reach support system.", "সিস্টেমে পৌঁছানো যাচ্ছে না।");

                // FAILURE path — matches Handle Complaint/Agent Result failure
                return complaintType == "agent_connect"
                    ? s.T(
                        $"❌ Agent request failed.\n\n{errMsg}\n\nSend *Y* to retry or *N* to cancel.",
                        $"❌ এজেন্ট অনুরোধ ব্যর্থ হয়েছে।\n\n{errMsg}\n\nপুনরায় চেষ্টা করতে *Y* পাঠান অথবা বাতিলের জন্য *N* পাঠান।")
                    : s.T(
                        $"❌ Submission failed.\n\n{errMsg}\n\nPlease try again — send *Y* to retry or *N* to cancel.",
                        $"❌ সাবমিট দেওয়া ব্যর্থ হয়েছে।\n\n{errMsg}\n\nঅনুগ্রহ করে আবার চেষ্টা করুন — পুনরায় সাবমিট দিতে *Y* পাঠান অথবা বাতিলের জন্য *N* পাঠান।");
            }

            var cid = result.ComplaintId ?? "";
            ClearComplaint(s);
            Transition(s, "MAIN_MENU");

            // SUCCESS path — matches Handle Complaint/Agent Result success
            return complaintType == "agent_connect"
                ? s.T(
                    $"✅ Agent request submitted successfully!\n\n" +
                    (cid != "" ? $"Ticket ID: {cid}\n\n" : "") +
                    "⏳ Our support team will contact you shortly.\n\n" +
                    "Type *menu* to return to main menu.",

                    $"✅ এজেন্ট অনুরোধ সফলভাবে পাঠানো হয়েছে!\n\n" +
                    (cid != "" ? $"টিকেট আইডি: {cid}\n\n" : "") +
                    "⏳ আমাদের সাপোর্ট টিম শীঘ্রই যোগাযোগ করবে।\n\n" +
                    "মূল মেনুতে ফিরতে *menu* টাইপ করুন।")
                : s.T(
                    $"Complaint submitted successfully!\n\n" +
                    $"Ticket ID: {cid}\n\n" +
                    "Our support team will contact you soon.\n\n" +
                    "Type *menu* to return to main menu.",

                    $"✅ অভিযোগ সফলভাবে সাবমিট হয়েছে!\n\n" +
                    $"টিকেট আইডি: {cid}\n\n" +
                    "আমাদের সাপোর্ট টিম শীঘ্রই যোগাযোগ করবে।\n\n" +
                    "মূল মেনুতে ফিরতে *menu* টাইপ করুন।");
        }

        // ─────────────────────────────────────────────────────────────────────
        // MEDIA — download from 360dialog, save to disk, save DB record
        // Fix 4: try-insert instead of ExistsAsync+Insert (saves 1 DB round-trip)
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

                var baseUrl = _config["App:BaseUrl"] ?? "http://chatbot.prangroup.com:8041";
                var fileUrl = $"{baseUrl}/wa-media/{subFolder}/{fileName}";

                // Fix 4: direct insert, catch duplicate (1 DB call instead of 2)
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
                    dbEx.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                    dbEx.Message.Contains("PRIMARY", StringComparison.OrdinalIgnoreCase) ||
                    dbEx.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _logger.LogDebug("[Bot] Media already exists, skipped: {Id}", messageId);
                }

                _logger.LogInformation("[Bot] Media saved: {File} ({Bytes}b)", fileName, bytes.Length);
                return messageId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Bot] SaveMedia failed for msgId={Id}", messageId);
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // MESSAGE BUILDERS — each matches the n8n node text character-for-character
        // ─────────────────────────────────────────────────────────────────────

        // Build Main Menu node
        private string BuildMainMenu(BotSession s)
        {
            Transition(s, "MAIN_MENU");
            ClearComplaint(s);
            var name = s.StaffName ?? "";
            return s.T(
                $"Hi, {name}! I'm Automated PRAN-RFL Sales Support\n\n" +
                "What would you like to do?\n\n" +
                "*1️⃣* Submit Complaint\n" +
                "*2️⃣* Check Complaint Status\n" +
                "*3️⃣* Connect with Support Agent\n\n" +
                "Reply *1*, *2* or *3*.",

                $"হ্যালো, {name}!\n\n" +
                "আপনি কী করতে চান?\n\n" +
                "*1️⃣* অভিযোগ সাবমিট দিন\n" +
                "*2️⃣* অভিযোগের স্ট্যাটাস দেখুন\n" +
                "*3️⃣* সাপোর্ট এজেন্টের সাথে কথা বলুন\n\n" +
                "*1*, *2* অথবা *3* পাঠান।");
        }

        // Ask Complaint Detail node (Bengali has double-space before *টেক্সট*)
        private string StartComplaint(BotSession s)
        {
            ClearComplaint(s);
            Transition(s, "AWAITING_COMPLAINT_DETAIL");
            return s.T(
                "Tell us your problem by sending *text*, *image*, or *voice*.\n\n" +
                "👉 Or to back main menu, send *0*",

                "আপনার সমস্যাটি  *টেক্সট*, *ছবি* বা *ভয়েস* দিয়ে জানান ।\n\n" +
                "👉 অথবা মূল মেনুতে ফিরে যেতে *0* লিখুন");
        }

        // Ask Complaint ID node
        private string StartStatusCheck(BotSession s)
        {
            Transition(s, "AWAITING_COMPLAINT_ID");
            return s.T(
                "Please enter your Complaint ID:\n\nExample: 13\n\n" +
                "Or to go back to main *menu* send *0*.",

                "আপনার অভিযোগ আইডি দিন:\n\nউদাহরণ: 13\n\n" +
                "মূল মেনুতে ফিরতে *0* পাঠান।");
        }

        // Connect Agent node
        private string StartAgentConnect(BotSession s)
        {
            Transition(s, "AWAITING_AGENT_CONFIRM");
            return s.T(
                "📞 Connect with Support Agent\n\n" +
                "👉 After confirmation, our support agent will contact you\n\n" +
                "Send *Y* to Confirm, *N* to Cancel\n" +
                "Or to go back to main menu, send *0*",

                "📞 সাপোর্ট এজেন্টের সাথে যোগাযোগ\n\n" +
                "👉 নিশ্চিত করলে সাপোর্ট এজেন্ট আপনার সাথে যোগাযোগ করবেন\n\n" +
                "*Y* — নিশ্চিত করুন\n" +
                "*N* — বাতিল করুন\n" +
                "👉 মূল মেনুতে যেতে *0* লিখুন");
        }

        // Show Complaint Confirm node (3-line prompt, no summary)
        private string BuildConfirmScreen(BotSession s)
        {
            return s.T(
                "To add more details, send *Voice*, *Image*, or *Text*\n" +
                "To submit your complaint, send *Y*\n" +
                "To cancel, send *N*",

                "আরও তথ্য যোগ করতে *ভয়েস*, *ছবি* বা *টেক্সট* পাঠান\n" +
                "অভিযোগ সাবমিট দিতে *Y* পাঠান\n" +
                "বাতিল করতে *N* পাঠান");
        }

        // Complaint Cancelled node (EN uses "menu" in quotes, no ❌)
        private string CancelComplaint(BotSession s)
        {
            ClearComplaint(s);
            Transition(s, "MAIN_MENU");
            return s.T(
                "Complaint cancelled.\nNo record has been saved.\n\nType *menu* to return to main menu.",
                "❌ অভিযোগ বাতিল হয়েছে।\n\nকোনো রেকর্ড সংরক্ষিত হয়নি।\n\nমূল মেনুতে ফিরতে *menu* টাইপ করুন।");
        }

        // Agent Cancelled node
        private string CancelAgent(BotSession s)
        {
            Transition(s, "MAIN_MENU");
            return s.T(
                "❌ Agent request cancelled.\n\nNo request has been submitted.\n\nType *menu* to return to main menu.",
                "❌ এজেন্ট অনুরোধ বাতিল হয়েছে।\n\nকোনো অনুরোধ পাঠানো হয়নি।\n\nমূল মেনুতে ফিরতে *menu* টাইপ করুন।");
        }

        // ─────────────────────────────────────────────────────────────────────
        // WELCOME MESSAGE WITH LOGO
        // Sends the PRAN-RFL logo as an image with the welcome text as caption.
        // Logo file lives at: wwwroot/images/pran-rfl-logo.jpg
        // Public URL is built from App:BaseUrl in appsettings.json.
        // Falls back to plain text automatically if image cannot be sent.
        // ─────────────────────────────────────────────────────────────────────

        private async Task SendWelcomeAsync(string phone, CancellationToken ct = default)
        {
            const string welcomeText =
                "🤝 *Welcome to PRAN-RFL Sales Support*\n\n" +
                "Please select your language:\n\n" +
                "1️⃣ English\n" +
                "2️⃣ বাংলা\n\n" +
                "Please reply with *1* or *2*";

            // Build the public URL for the logo from App:BaseUrl.
            // Logo file must be placed at: wwwroot/images/pran-rfl-logo.jpg
            // It is served as a static file by app.UseStaticFiles() in Program.cs.
            var baseUrl = _config["App:BaseUrl"]?.TrimEnd('/') ?? "https://chatbot.prangroup.com";
            var logoUrl = $"{baseUrl}/images/pran-rfl-logo.jpg";

            // SendImageAsync falls back to plain text automatically if URL is unreachable
            await _dialog.SendImageAsync(phone, logoUrl, welcomeText, ct);
        }

        // ─────────────────────────────────────────────────────────────────────
        // HELPERS
        // ─────────────────────────────────────────────────────────────────────

        private static void Transition(BotSession s, string newState)
        {
            s.PreviousState = s.State;
            s.State = newState;
        }

        private static void ClearComplaint(BotSession s)
        {
            s.ComplaintDescription = string.Empty;
            s.ComplaintVoices = new();
            s.ComplaintImages = new();
        }

        // PERF: load session from cache first, SQL fallback on miss.
        // Uses sliding expiration so active users never hit SQL mid-conversation.
        private async Task<BotSession> LoadSessionAsync(string phone)
        {
            if (_cache.TryGetValue($"session:{phone}", out BotSession? cached) && cached != null)
            {
                _logger.LogDebug("[Bot] Session cache HIT {Phone}", phone);
                return cached;
            }

            _logger.LogDebug("[Bot] Session cache MISS {Phone} — loading SQL", phone);
            var row = await _sessionSvc.GetSessionAsync(phone);
            var session = BotSession.Load(phone, row.TempData);
            if (session.State == "INIT" && row.CurrentStep != "INIT")
                session.State = row.CurrentStep;

            // CHANGED: sliding expiration (was fixed 30 min)
            // Active users stay warm; idle users expire naturally after 60 min of silence
            var opts = new MemoryCacheEntryOptions()
                .SetSlidingExpiration(TimeSpan.FromMinutes(60));
            _cache.Set($"session:{phone}", session, opts);
            return session;
        }

        // PERF: update cache immediately so next message reads fresh state
        // without a SQL round-trip, then persist to DB in background.
        private async Task PersistSessionAsync(BotSession s, string rawText)
        {
            // CHANGED: sliding expiration (was fixed 30 min)
            var opts = new MemoryCacheEntryOptions()
                .SetSlidingExpiration(TimeSpan.FromMinutes(60));
            _cache.Set($"session:{s.Phone}", s, opts);

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
                _logger.LogError(ex, "[Bot] PersistSession failed for {Phone}", s.Phone);
            }
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
    // MESSAGE PARSER — exact replica of Parse Message n8n node
    // ─────────────────────────────────────────────────────────────────────────
    public static class MessageParser
    {
        public static IncomingMessage? Parse(JsonElement body)
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
                    rawText = Regex.Replace(
                        (bodyEl.GetString() ?? "").Trim().ToLowerInvariant(),
                        @"[\u200B-\u200D\uFEFF]", "");
                }
                else if (msgType is "audio" or "image")
                {
                    var src = msgType == "image" ? "image" : "audio";
                    if (msg.TryGetProperty(src, out var med) &&
                        med.TryGetProperty("caption", out var cap))
                        rawText = (cap.GetString() ?? "").Trim().ToLowerInvariant();
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

                return new IncomingMessage
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
