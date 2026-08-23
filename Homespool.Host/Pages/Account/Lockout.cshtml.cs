// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous] // Reached by an account that has just been refused a sign-in.
public class LockoutModel : PageModel
{
    public void OnGet()
    {
    }
}
