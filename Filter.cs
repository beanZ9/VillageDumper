namespace VillageDumper {
    public class Filter {
        public string DisplayName { get; set; }
        public string Query { get; set; }
        public bool Include { get; set; }
        public bool Exclude { get; set; }
        public bool CanBeExcluded { get; set; } = true;
        public bool CanBeEdited { get; set; } = true;
    }
}
