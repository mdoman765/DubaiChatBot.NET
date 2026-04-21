namespace API.Dtos
{
    // ────────────────────────────────────────────────
    // Used in GET /api/employees/profile or similar
    // ────────────────────────────────────────────────
    public class ProfileDto
    {
        public string Id { get; set; } = string.Empty;           // ← added: very useful for frontend
        public string FullName { get; set; } = string.Empty;     // ← renamed from Name (more consistent)
        public string Email { get; set; } = string.Empty;
        public string? Phone { get; set; }                       // ← nullable is better
        public string? AccountNumber { get; set; }               // ← nullable
        //public string RoleName { get; set; } = "Employee";       // default or from claims/roles
        public DateTime? LastLoginDate { get; set; }
        public DateTime AddedDate { get; set; }

        // Optional extras you might want later
        // public string? DepartmentName { get; set; }
        // public bool IsActive { get; set; }
    }

    // ────────────────────────────────────────────────
    // Used in PUT /api/employees/profile or /api/auth/change-password
    // ────────────────────────────────────────────────
    public class UpdateProfileDto
    {
        public string? Phone { get; set; }          // nullable → only update if provided
        public string? Email { get; set; }          // nullable + validation needed
        public string? Password { get; set; }       // nullable → only if changing password

        // Optional: if you allow name change
        // public string? FullName { get; set; }
    }

    // Bonus: if you return richer data after login
    public class LoginResponseDto
    {
        public string AccessToken { get; set; } = string.Empty;
        public int ExpiresInSeconds { get; set; }
        public string? RefreshToken { get; set; }   // if you implement refresh tokens

        public ProfileDto User { get; set; } = null!;
    }

    // Optional: for employee creation (POST api/employees)
    
}