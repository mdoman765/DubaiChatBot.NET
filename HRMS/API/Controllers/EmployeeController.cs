using API.Dtos;
using BCrypt.Net;
using BLL.Services.Interfaces;
using BLL.Services.Interfaces;
using DAL.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Models.Entities;
using System.Security.Claims;
using System.Threading.Tasks;
namespace HRMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EmployeesController : ControllerBase
    {
        private readonly IEmployeeService _employeeService;
        private readonly UserManager<IdentityUser> _userManager;
        private readonly ApplicationDbContext _context;

        public EmployeesController(IEmployeeService employeeService, UserManager<IdentityUser> userManager, ApplicationDbContext context)
        {
            _userManager = userManager;
            _employeeService = employeeService;
            _context = context;
        }

        // GET: api/employees
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<IEnumerable<Employee>>> GetAll()
        {
            var employees = await _employeeService.GetAllAsync();
            return Ok(employees);
        }

        // GET: api/employees/5
        [HttpGet("{id:int}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<Employee>> GetById(int id)
        {
            var employee = await _employeeService.GetByIdAsync(id);

            if (employee == null)
            {
                return NotFound();
            }

            return Ok(employee);
        }

        // POST: api/employees
        // POST: api/employees
        [HttpPost]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<Employee>> Create([FromBody] EmployeeCreateDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            // Additional basic validation
            if (string.IsNullOrWhiteSpace(dto.Name))
                ModelState.AddModelError("Name", "Name is required");
            if (string.IsNullOrWhiteSpace(dto.Email))
                ModelState.AddModelError("Email", "Email is required");
            if (dto.DepartmentId <= 0)
                ModelState.AddModelError("DepartmentId", "Valid department required");

            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            int currentUserId = 12; // TODO: Get from User.Claims later

            // Hash password
            string password = dto.Password ?? "Welcome@123";
            dto.Password = password;
            string hashedPassword = BCrypt.Net.BCrypt.HashPassword(password);

            // Create User
            var user = new User
            {
                Username = dto.Email,           // or generate from name/email
                Email = dto.Email,
                PasswordHash = hashedPassword,
                RoleId = 2,                     // employee role
                AddedDate = DateTime.UtcNow,
                AddedBy = currentUserId,
                IsActive = true
            };

            // Create Employee
            var employee = new Employee
            {
                Name = dto.Name,
                Email = dto.Email,
                Phone = dto.Phone,
                DepartmentId = dto.DepartmentId,
                AccountNumber = dto.AccountNumber,
                AddedDate = DateTime.UtcNow,
                AddedBy = currentUserId,
                IsActive = true
                // If Employee has UserId / CreatedByUser navigation → set it here
                // e.g. User = user   (if navigation property exists)
            };
            try
            {
                var result = await _userManager.CreateAsync(new IdentityUser { UserName = dto.Name, Email = dto.Email }, dto.Password);

            }
            catch
            {
            }

            try
            {
                // Save both in one transaction
                var createdUser = await _employeeService.CreateUserAsync(user, currentUserId);

                // Link employee to the newly created user if needed
                // employee.CreatedByUserId = createdUser.Id;   // if you have this field

                var createdEmployee = await _employeeService.CreateAsync(employee, currentUserId);

                return CreatedAtAction(nameof(GetById), new { id = createdEmployee.Id }, createdEmployee);
            }
            catch (DbUpdateException dbEx)
            {
                // Handle unique constraint violation (e.g. duplicate email)
                if (dbEx.InnerException?.Message.Contains("unique") == true ||
                    dbEx.InnerException?.Message.Contains("duplicate") == true)
                {
                    ModelState.AddModelError("Email", "Email already exists");
                    return BadRequest(ModelState);
                }

                // Log if needed
                // _logger.LogError(dbEx, "Database error while creating employee/user");
                return StatusCode(500, "Database error occurred");
            }
            catch (Exception ex)
            {
                // _logger.LogError(ex, "Unexpected error while creating employee");
                return StatusCode(500, "An unexpected error occurred");
            }
        }

        // PUT: api/employees/5
        //[HttpPut("{id:int}")]
        //[ProducesResponseType(StatusCodes.Status204NoContent)]
        //[ProducesResponseType(StatusCodes.Status400BadRequest)]
        //[ProducesResponseType(StatusCodes.Status404NotFound)]
        //public async Task<IActionResult> Update(int id, [FromBody] Employee employee)
        //{
        //    if (employee == null || employee.Id <= 0)
        //    {
        //        return BadRequest("Invalid employee data.");
        //    }

        //    if (id != employee.Id)
        //    {
        //        return BadRequest("ID in URL and body must match.");
        //    }

        //    // Basic validation
        //    if (string.IsNullOrWhiteSpace(employee.Name))
        //    {
        //        ModelState.AddModelError("Name", "Name is required.");
        //    }

        //    if (string.IsNullOrWhiteSpace(employee.Email))
        //    {
        //        ModelState.AddModelError("Email", "Email is required.");
        //    }

        //    if (employee.DepartmentId <= 0)
        //    {
        //        ModelState.AddModelError("DepartmentId", "Valid department is required.");
        //    }

        //    if (!ModelState.IsValid)
        //    {
        //        return BadRequest(ModelState);
        //    }

        //    // In real app → current user ID should come from claims / identity
        //    int currentUserId = 1;  // ← TODO: Get from HttpContext / UserManager / Claims

        //    var success = await _employeeService.UpdateAsync(employee, currentUserId);

        //    if (!success)
        //    {
        //        return NotFound();
        //    }

        //    return NoContent();
        //}

        // Add this DTO class in API/Dtos folder
        public class EmployeeUpdateDto
        {
            public int Id { get; set; }
            public string? Name { get; set; }
            public string? Email { get; set; }
            public string? Phone { get; set; }
            public int DepartmentId { get; set; }
            public string? AccountNumber { get; set; }
            public bool IsActive { get; set; }
        }

        // Update PUT method
        [HttpPut("{id:int}")]
        public async Task<IActionResult> Update(int id, [FromBody] EmployeeUpdateDto dto)
        {
            if (dto == null || dto.Id != id)
                return BadRequest("ID mismatch or invalid data");

            if (string.IsNullOrWhiteSpace(dto.Name))
                ModelState.AddModelError("Name", "Name is required");

            if (string.IsNullOrWhiteSpace(dto.Email))
                ModelState.AddModelError("Email", "Email is required");

            if (dto.DepartmentId <= 0)
                ModelState.AddModelError("DepartmentId", "Valid department required");

            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            int currentUserId = 1; // TODO: from claims

            var employee = new Employee
            {
                Id = id,
                Name = dto.Name,
                Email = dto.Email,
                Phone = dto.Phone,
                DepartmentId = dto.DepartmentId,
                AccountNumber = dto.AccountNumber,
                IsActive = dto.IsActive
                // Preserve AddedDate / AddedBy from DB
            };

            var success = await _employeeService.UpdateAsync(employee, currentUserId);

            if (!success)
                return NotFound();

            return NoContent();
        }

        // DELETE: api/employees/5  (soft delete)
        [HttpDelete("{id:int}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Delete(int id)
        {
            if (id <= 0)
            {
                return BadRequest("Invalid ID.");
            }

            // In real app → current user ID should come from claims / identity
            int currentUserId = 1;  // ← TODO: Get from HttpContext / UserManager / Claims

            var success = await _employeeService.SoftDeleteAsync(id, currentUserId);

            if (!success)
            {
                return NotFound();
            }

            return NoContent();
        }
        [HttpGet("profile")]
        [Authorize]
        public async Task<ActionResult<ProfileDto>> GetProfile()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
                return NotFound();

            // If using Identity roles:
            var roles = await _userManager.GetRolesAsync(user);
            var roleName = roles.FirstOrDefault() ?? "Employee";

            // If still using custom Role entity:
            // var roleName = user.CustomRole?.Name ?? "Employee";

            var profile = new ProfileDto
            {
                Id = user.Id,
               // FullName = user.FullName,
                Email = user.Email!,
              //  Phone = user.Phone,
              //  AccountNumber = user.AccountNumber,
                //RoleName = roleName,
             //   LastLoginDate = user.LastLoginDate,
              //  AddedDate = user.AddedDate
            };

            return Ok(profile);
        }
        [HttpPut("profile")]
        [Authorize]
        public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileDto dto)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return NotFound();

            bool changed = false;

            if (!string.IsNullOrWhiteSpace(dto.Phone))
            {
               // user.Phone = dto.Phone;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(dto.Email) && dto.Email != user.Email)
            {
                if (await _userManager.FindByEmailAsync(dto.Email) != null)
                    return BadRequest("Email already in use");

                var emailToken = await _userManager.GenerateChangeEmailTokenAsync(user, dto.Email);
                var emailResult = await _userManager.ChangeEmailAsync(user, dto.Email, emailToken);
                if (!emailResult.Succeeded) return BadRequest(emailResult.Errors);

                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(dto.Password))
            {
                var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
                var passwordResult = await _userManager.ResetPasswordAsync(user, resetToken, dto.Password);
                if (!passwordResult.Succeeded) return BadRequest(passwordResult.Errors);

                changed = true;
            }

            if (changed)
            {
               // user.UpdatedDate = DateTime.UtcNow;
                await _userManager.UpdateAsync(user);
            }

            return NoContent();
        }
    }
}
