// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Homespool.Host.Pages.Account;

[AllowAnonymous] // Shown when authorisation fails, so it cannot itself require authorisation.
public class AccessDeniedModel : PageModel
{
    public void OnGet()
    {
    }
}
