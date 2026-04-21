using System.Net.Http.Headers;
using System.Text.Json;
using crud_app_backend.DTOs;
using crud_app_backend.Models;
using crud_app_backend.Repositories;

namespace crud_app_backend.Services
{
    public class WhatsAppComplaintService : IWhatsAppComplaintService
    {
        private readonly IWhatsAppComplaintRepository _complaintRepo;
        private readonly IWhatsAppMessageRepository _messageRepo;
        private readonly IWebHostEnvironment _env;
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<WhatsAppComplaintService> _logger;

        public WhatsAppComplaintService(
            IWhatsAppComplaintRepository complaintRepo,
            IWhatsAppMessageRepository messageRepo,
            IWebHostEnvironment env,
            IHttpClientFactory httpFactory,
            IConfiguration config,
            ILogger<WhatsAppComplaintService> logger)
        {
            _complaintRepo = complaintRepo;
            _messageRepo = messageRepo;
            _env = env;
            _httpFactory = httpFactory;
            _config = config;
            _logger = logger;
        }

        public async Task<SubmitComplaintResponseDto> SubmitAsync(
            SubmitComplaintRequestDto req, CancellationToken ct)
        {
            // BUG FIX: wrap entire method in try-catch.
            // Without this, any unhandled exception (e.g. DB insert failure after
            // CRM succeeds) propagates to BotService.ProcessAsync, gets caught by
            // the outer try-catch, logged silently — user only ever sees the ACK
            // message and never gets the success/failure reply.
            try
            {
                return await SubmitInternalAsync(req, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Complaint] SubmitAsync unhandled exception — type={T} phone={P}",
                    req.ComplaintType, req.WhatsappPhone);
                return new SubmitComplaintResponseDto
                {
                    Success = false,
                    ComplaintId = null,
                    ErrorMessage = "An unexpected error occurred. Please try again.",
                    Message = "Unhandled exception in SubmitAsync",
                };
            }
        }

        private async Task<SubmitComplaintResponseDto> SubmitInternalAsync(
            SubmitComplaintRequestDto req, CancellationToken ct)
        {
            _logger.LogInformation(
                "[Complaint] Submit — type={T} staff={S} phone={P} voices={V} images={I}",
                req.ComplaintType, req.StaffId, req.WhatsappPhone,
                req.VoiceMessageIds.Count, req.ImageMessageIds.Count);

            // ── STEP 1: Load voice files ──────────────────────────────────────
            // FIX: EF Core DbContext is NOT thread-safe. Task.WhenAll with multiple
            // GetByMessageIdAsync calls on the same DbContext causes:
            // "A second operation was started on this context before a previous one completed"
            // This was throwing and hitting the outer catch → "unexpected error" for users.
            // Solution: sequential foreach — await each DB call one at a time.
            var validVoiceIds = req.VoiceMessageIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

            var voiceFiles = new List<(string FileName, string MimeType, byte[] Data)>();
            var voiceMsgMap = new Dictionary<string, WhatsAppMessage?>();
            foreach (var id in validVoiceIds)
            {
                var waMsg = await _messageRepo.GetByMessageIdAsync(id, ct);
                voiceMsgMap[id] = waMsg;
                var file = ReadFileDirect(id, "audio", waMsg?.MimeType ?? "audio/ogg");
                if (file is not null)
                {
                    voiceFiles.Add(file.Value);
                    _logger.LogInformation("[Complaint] Voice loaded: {F} ({B} bytes)",
                        file.Value.FileName, file.Value.Data.Length);
                }
                else
                    _logger.LogWarning("[Complaint] Voice file not found on disk: msgId={Id}", id);
            }

            // ── STEP 2: Load image files ──────────────────────────────────────
            // Same fix: sequential foreach instead of Task.WhenAll.
            var validImageIds = req.ImageMessageIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

            var imageFiles = new List<(string FileName, string MimeType, byte[] Data)>();
            var imageMsgMap = new Dictionary<string, WhatsAppMessage?>();
            foreach (var id in validImageIds)
            {
                var waMsg = await _messageRepo.GetByMessageIdAsync(id, ct);
                imageMsgMap[id] = waMsg;
                var file = ReadFileDirect(id, "images", waMsg?.MimeType ?? "image/jpeg");
                if (file is not null)
                {
                    imageFiles.Add(file.Value);
                    _logger.LogInformation("[Complaint] Image loaded: {F} ({B} bytes)",
                        file.Value.FileName, file.Value.Data.Length);
                }
                else
                    _logger.LogWarning("[Complaint] Image file not found on disk: msgId={Id}", id);
            }

            // ── STEP 3: Send to CRM FIRST ─────────────────────────────────────
            // Only save to DB if CRM succeeds.
            var (crmTicketId, crmError) = await SendToCrmAsync(req, voiceFiles, imageFiles);

            if (crmTicketId is null)
            {
                _logger.LogWarning("[Complaint] CRM failed — skipping DB save. Error: {E}", crmError);
                return new SubmitComplaintResponseDto
                {
                    Success = false,
                    ComplaintId = null,
                    ErrorMessage = crmError ?? "Could not reach support team. Please try again.",
                    Message = "CRM submission failed",
                };
            }

            // ── STEP 4: CRM succeeded — now save to YOUR DB ───────────────────
            var complaint = new WhatsAppComplaint
            {
                WhatsappPhone = req.WhatsappPhone,
                StaffId = req.StaffId,
                StaffName = req.Name,
                OfficialPhone = req.OfficialPhone,
                Designation = req.Designation,
                Dept = req.Dept,
                GroupName = req.GroupName,
                Company = req.Company,
                LocationName = req.LocationName,
                Email = req.Email,
                Description = req.Description,
                ComplaintType = req.ComplaintType,
                CrmTicketId = crmTicketId,
                Status = "open",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            await _complaintRepo.InsertAsync(complaint, ct);

            var complaintNumber = $"PR{complaint.Id:D5}";
            await _complaintRepo.UpdateComplaintNumberAsync(complaint.Id, complaintNumber, ct);

            // ── STEP 5: Save media rows ───────────────────────────────────────
            // FIX: sequential inserts — same DbContext thread-safety reason as above.
            // voiceMsgMap and imageMsgMap were built in Steps 1 & 2 so no extra DB reads.
            foreach (var msgId in validVoiceIds)
            {
                voiceMsgMap.TryGetValue(msgId, out var waMsg);
                await _complaintRepo.InsertMediaAsync(new WhatsAppComplaintMedia
                {
                    ComplaintId = complaint.Id,
                    MessageId = msgId,
                    MediaType = "voice",
                    FileUrl = waMsg?.FileUrl,
                    MimeType = waMsg?.MimeType ?? "audio/ogg",
                }, ct);
            }

            foreach (var msgId in validImageIds)
            {
                imageMsgMap.TryGetValue(msgId, out var waMsg);
                await _complaintRepo.InsertMediaAsync(new WhatsAppComplaintMedia
                {
                    ComplaintId = complaint.Id,
                    MessageId = msgId,
                    MediaType = "image",
                    FileUrl = waMsg?.FileUrl,
                    MimeType = waMsg?.MimeType ?? "image/jpeg",
                    Caption = waMsg?.Caption,
                }, ct);
            }

            _logger.LogInformation(
                "[Complaint] Saved — complaintNumber={N} crmTicketId={C} voices={V} images={I}",
                complaintNumber, crmTicketId,
                req.VoiceMessageIds.Count, req.ImageMessageIds.Count);

            return new SubmitComplaintResponseDto
            {
                Success = true,
                ComplaintId = crmTicketId,
                Message = req.ComplaintType == "agent_connect"
                    ? "Support agent request submitted successfully"
                    : "Complaint submitted to support team",
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // Read file directly from wwwroot/wa-media/{subFolder}/{messageId}.ext
        // ─────────────────────────────────────────────────────────────────────
        private (string FileName, string MimeType, byte[] Data)?
            ReadFileDirect(string messageId, string subFolder, string mimeType)
        {
            try
            {
                var ext = MimeToExt(mimeType);
                var folder = Path.Combine(_env.WebRootPath, "wa-media", subFolder);
                var exactPath = Path.Combine(folder, $"{messageId}{ext}");

                if (File.Exists(exactPath))
                {
                    var bytes = File.ReadAllBytes(exactPath);
                    return ($"{messageId}{ext}", mimeType, bytes);
                }

                // Fallback: glob for any file starting with the messageId
                var matches = Directory.Exists(folder)
                    ? Directory.GetFiles(folder, $"{messageId}*")
                    : Array.Empty<string>();

                if (matches.Length > 0)
                {
                    var bytes = File.ReadAllBytes(matches[0]);
                    var actualExt = Path.GetExtension(matches[0]);
                    return (Path.GetFileName(matches[0]), ExtToMime(actualExt, mimeType), bytes);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Complaint] ReadFileDirect failed — msgId={Id}", messageId);
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST multipart/form-data to CRM.
        // Returns (crmTicketId, null) on success.
        // Returns (null, errorMessage) on failure.
        // CHANGED: internal CTS reduced to 55s (just under the 60s HttpClient
        // timeout set in Program.cs) so we always surface a clean user-facing
        // message rather than an HttpClient OperationCanceledException.
        // ─────────────────────────────────────────────────────────────────────
        private async Task<(string? TicketId, string? Error)> SendToCrmAsync(
            SubmitComplaintRequestDto req,
            List<(string FileName, string MimeType, byte[] Data)> voiceFiles,
            List<(string FileName, string MimeType, byte[] Data)> imageFiles)
        {
            var crmUrl = _config["Crm:SubmitUrl"];
            if (string.IsNullOrWhiteSpace(crmUrl))
            {
                _logger.LogWarning("[Complaint] Crm:SubmitUrl not configured");
                return (null, "CRM is not configured. Please contact system admin.");
            }

            // CHANGED: was 120s — now 55s, just under the 60s HttpClient timeout
            // in Program.cs, so our catch block always fires before HttpClient cancels.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(55));

            try
            {
                using var form = new MultipartFormDataContent();

                void Text(string key, string? val)
                    => form.Add(new StringContent(val ?? string.Empty), key);

                Text("chat_phone", req.WhatsappPhone);
                Text("staff_id", req.StaffId);
                Text("name", req.Name);
                Text("phone", req.OfficialPhone);
                Text("designation", req.Designation);
                Text("department", req.Dept);
                Text("groupname", req.GroupName);
                Text("company", req.Company);
                Text("locationname", req.LocationName);
                Text("email", req.Email);
                Text("description", req.Description);
                Text("ticket_type", req.ComplaintType == "agent_connect"
                    ? "CONNECT_TO_AGENT"
                    : "COMPLAIN");

                foreach (var (fileName, mimeType, data) in voiceFiles)
                {
                    var content = new ByteArrayContent(data);
                    content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
                    form.Add(content, "voice_file[]", fileName);
                }

                foreach (var (fileName, mimeType, data) in imageFiles)
                {
                    var content = new ByteArrayContent(data);
                    content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
                    form.Add(content, "images[]", fileName);
                }

                var client = _httpFactory.CreateClient("CrmClient");
                var response = await client.PostAsync(crmUrl, form, cts.Token);
                var body = await response.Content.ReadAsStringAsync(cts.Token);

                _logger.LogInformation("[Complaint] CRM {Code}: {Body}",
                    (int)response.StatusCode,
                    body.Length > 800 ? body[..800] : body);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[Complaint] CRM HTTP {Code}: {Body}",
                        (int)response.StatusCode, body);
                    return (null, $"Support system returned error {(int)response.StatusCode}. Please try again.");
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var sv) &&
                    sv.GetString()?.ToLower() != "success")
                {
                    var crmMsg = root.TryGetProperty("message", out var mv) ? mv.GetString() : null;
                    var errMsg = string.IsNullOrWhiteSpace(crmMsg)
                        ? "Support system could not process the request. Please try again."
                        : crmMsg;
                    _logger.LogWarning("[Complaint] CRM status={S} message={M}", sv.GetString(), crmMsg);
                    return (null, errMsg);
                }

                if (root.TryGetProperty("data", out var dataEl) &&
                    dataEl.TryGetProperty("id", out var idEl))
                {
                    var crmId = idEl.ValueKind == JsonValueKind.Number
                        ? idEl.GetInt32().ToString()
                        : idEl.GetString();

                    _logger.LogInformation("[Complaint] CRM ticket created — id={Id}", crmId);
                    return (crmId, null);
                }

                return (null, "Support system responded but did not return a ticket ID. Please try again.");
            }
            catch (OperationCanceledException)
            {
                // CHANGED: clear user-facing timeout message instead of silent hang
                _logger.LogError("[Complaint] CRM call timed out after 55s");
                return (null, "⏱ Support system is taking too long to respond. Please try again in a moment.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Complaint] CRM call threw exception");
                return (null, "Could not reach support system. Please try again.");
            }
        }

        private static string MimeToExt(string mime) => mime switch
        {
            "audio/ogg" => ".ogg",
            "audio/mpeg" => ".mp3",
            "audio/mp4" => ".m4a",
            "audio/wav" => ".wav",
            "audio/opus" => ".opus",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".bin"
        };

        private static string ExtToMime(string ext, string fallback) =>
            ext.ToLowerInvariant() switch
            {
                ".ogg" => "audio/ogg",
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".wav" => "audio/wav",
                ".opus" => "audio/opus",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                _ => fallback
            };
    }
}
