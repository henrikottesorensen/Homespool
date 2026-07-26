using System;

using Microsoft.AspNetCore.Identity;

namespace PrinterService.Model.Entities;

public class PSUser : IdentityUser<long>
{
    public PSUser()
    {
        SecurityStamp = Guid.NewGuid().ToString();
    }

    public PSUser(string userName)
        : this()
    {
        UserName = userName;
    }
}
