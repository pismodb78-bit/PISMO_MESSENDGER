namespace PISMO
{
    /// <summary>Статус звонка из БД.</summary>
    public enum CallStatus { Ringing, Active, Ended, Rejected, Missed }

    public class CallSessionInfo
    {
        public int         Id          { get; set; }
        public int         CallerId    { get; set; }
        public string      CallerName  { get; set; } = "";
        public int?        CalleeId    { get; set; }
        public int?        GroupId     { get; set; }
        public CallStatus  Status      { get; set; }
        public bool        HasVideo    { get; set; }
        public bool        HasScreen   { get; set; }
        public string      CallerIp    { get; set; }
        public int?        CallerPort  { get; set; }
        public string      CalleeIp    { get; set; }
        public int?        CalleePort  { get; set; }
        public System.DateTime CreatedAt { get; set; }
    }
}
