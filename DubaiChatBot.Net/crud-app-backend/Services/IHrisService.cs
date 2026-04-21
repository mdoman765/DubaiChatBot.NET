namespace crud_app_backend.Bot.Services
{
    public interface IHrisService
    {
        /// <summary>
        /// Verify a staff ID against HRIS.
        /// Returns HrisStaffData on success (Status="Active"),
        /// an object with non-Active Status if found-but-inactive,
        /// or null if the staff ID does not exist / network error.
        /// </summary>
        Task<HrisStaffData?> VerifyAsync(string staffId,
            CancellationToken ct = default);
    }

    public class HrisStaffData
    {
        public string Id           { get; set; } = string.Empty;
        public string Name         { get; set; } = string.Empty;
        public string ContactNo    { get; set; } = string.Empty;
        public string Email        { get; set; } = string.Empty;
        public string Designation  { get; set; } = string.Empty;
        public string Department   { get; set; } = string.Empty;
        public string GroupName    { get; set; } = string.Empty;
        public string Company      { get; set; } = string.Empty;
        public string LocationName { get; set; } = string.Empty;
        /// <summary>"Active" | "Inactive" | other HRIS status values.</summary>
        public string Status       { get; set; } = string.Empty;
    }
}
