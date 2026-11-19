namespace BillPro.Models
{
    /// <summary>
    /// Developer: the single account only you know the password to. Only
    /// role that can open Company Details or create Admin-level users.
    /// Admin: can create Normal users, do menu customization, change
    /// passwords. Cannot touch Company Details.
    /// Normal: created by Admin/Developer. CanEdit/CanDelete are set per
    /// account at creation time (see app_users.can_edit / can_delete).
    /// Cannot see User Registration, Menu Customization, or Change
    /// Password screens.
    /// </summary>
    public enum UserRole
    {
        Developer,
        Admin,
        Normal
    }
}