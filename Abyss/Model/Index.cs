using SQLite;

namespace Abyss.Model;

[Table("Index")]
public class Index
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    
    // 0: folder, 1: video, 2: comic
    public int Type { get; set; }
    
    // The resources referenced by the index node, the format is "Video, Class, ID", "Comic, ID"
    // eg: "Video,Animation,12"
    // eg: "Comic,9"
    // eg: "Video,Movie,45"
    // When a directory node references an actual resource, the resource is treated as the cover page of the directory
    public string Reference { get; set; } = "";
    
    // The direct successor node of this node
    // eg: "1,2,3,4"
    public string Children { get; set; } = "";
}