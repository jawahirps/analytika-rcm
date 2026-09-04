using System.ComponentModel.DataAnnotations;

namespace Analytika.Models.ViewModels;

/// <summary>
/// Self-service password reset. Until now only an administrator could reset a password
/// (AdminController.ResetPassword), so every forgotten password was a support request —
/// unworkable for a SaaS, and a daily cost even for the current invite-only setup.
/// </summary>
public class ForgotPasswordViewModel
{
    [Required(ErrorMessage = "Enter the email address you sign in with.")]
    [EmailAddress(ErrorMessage = "That does not look like an email address.")]
    [Display(Name = "Email address")]
    public string Email { get; set; } = string.Empty;
}

public class ResetPasswordViewModel
{
    [Required]
    public string Email { get; set; } = string.Empty;

    /// <summary>Identity-issued, single-use and time-limited. Never a guessable id.</summary>
    [Required]
    public string Token { get; set; } = string.Empty;

    [Required(ErrorMessage = "Choose a new password.")]
    [StringLength(128, MinimumLength = 12,
        ErrorMessage = "Use at least 12 characters — length protects you more than symbols do.")]
    [DataType(DataType.Password)]
    [Display(Name = "New password")]
    public string Password { get; set; } = string.Empty;

    [DataType(DataType.Password)]
    [Display(Name = "Confirm new password")]
    [Compare(nameof(Password), ErrorMessage = "The two passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}
