namespace GoldenISOBuilder.Models;

public class RegistryEntry
{
    public string Hive      { get; set; } = "HKLM";     // HKLM or HKCU
    public string KeyPath   { get; set; } = "";
    public string ValueName { get; set; } = "";
    public string Type      { get; set; } = "REG_SZ";   // REG_SZ, REG_DWORD, REG_QWORD, REG_BINARY, REG_MULTI_SZ, REG_EXPAND_SZ
    public string Data      { get; set; } = "";
    public string Operation { get; set; } = "SET";      // SET or DELETE
}
