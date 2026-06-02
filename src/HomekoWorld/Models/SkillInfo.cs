namespace HomekoWorld.Models;

public class SkillInfo
{
    public string  Id       { get; set; } = "";
    public string  Class    { get; set; } = "";
    public string  NameTr   { get; set; } = "";
    public string  NameEn   { get; set; } = "";
    public string  IconPath { get; set; } = ""; // relative path under Assets/Skills/
    public ulong   Hash     { get; set; }        // pHash — populated at runtime
}
