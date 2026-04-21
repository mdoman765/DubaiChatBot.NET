namespace crud_app_backend.DTOs
{
    public class SessionResponse
    {
        public string Phone { get; set; } = default!;
        public string CurrentStep { get; set; } = "INIT";
        public string PreviousStep { get; set; } = "INIT";
        public string TempData { get; set; } = "{}";
        public bool PendingReport { get; set; } = false;
        public bool PendingShopReg { get; set; } = false;
        public DateTime UpdatedAt { get; set; }
        public bool IsNew { get; set; }
    }

}
