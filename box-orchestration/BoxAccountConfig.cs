namespace boxaccountorchestration
{
    public interface IBoxAccountConfig
    { 
        string BoxConfigJson { get; set; }        
    }
    public class BoxAccountConfig : IBoxAccountConfig
    {
        public string BoxConfigJson { get; set; }
    }
}