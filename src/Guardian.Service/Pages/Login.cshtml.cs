using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Guardian.Service.Pages;

public class LoginModel : PageModel
{
    public string? Error { get; private set; }
    public string ReturnUrl { get; private set; } = "/";
    public void OnGet(string? error, string? returnUrl) { Error = error; ReturnUrl = string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') ? "/" : returnUrl; }
}
