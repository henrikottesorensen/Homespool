using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PrinterService.Api.Test;

public class JsonDotNetDeserialiseTests
{
    [Theory]
    [InlineData("example.newlinedelimited.json")]
    [InlineData("example.concatenated.json")]

    public void MultiObjectJsonParses(string filename)
    {
        using Stream file = File.OpenRead(filename);
        using JsonTextReader reader = new(new StreamReader(file));
        
        reader.SupportMultipleContent = true;

        while (reader.Read())
        {
            JsonSerializer serializer = new();
            
            JObject? o = serializer.Deserialize<JObject>(reader);
            
            Assert.True(o.ContainsKey("state"));
        }
    }
    
    [Theory]
    [InlineData("example.newlinedelimited.json")]
    [InlineData("example.concatenated.json")]

    public void AwkwardSizedReadsParses(string filename)
    {
        using Stream file = File.OpenRead(filename);
        using BufferedStream bufferedStream =  new(file, 3);
        using JsonTextReader reader = new(new StreamReader(bufferedStream));
        
        reader.SupportMultipleContent = true;

        while (reader.Read())
        {
            JsonSerializer serializer = new();
            
            JObject? o = serializer.Deserialize<JObject>(reader);
            
            Assert.True(o.ContainsKey("state"));
        }
    }
}
