using System;

using Microsoft.AspNetCore.Identity;

namespace Homespool.Model.Entities;

public class HSUser : IdentityUser<long>
{
    public HSUser()
    {
        SecurityStamp = Guid.NewGuid().ToString();
    }

    public HSUser(string userName)
        : this()
    {
        UserName = userName;
    }
}
