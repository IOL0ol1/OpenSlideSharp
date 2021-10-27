using System.Globalization;
using System.Windows.Controls;

namespace SlideLibrary.Demo
{
    public class NavigatorValidationRule : ValidationRule
    {
        public NavigatorValidationRule() : this(ValidationStep.RawProposedValue, validatesOnTargetUpdated: false)
        { }

        public NavigatorValidationRule(ValidationStep validationStep, bool validatesOnTargetUpdated) : base(validationStep, validatesOnTargetUpdated)
        {
        }

        public override ValidationResult Validate(object value, CultureInfo cultureInfo)
        {
            if (value is string str && str.Split(new char[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries) is string[] strs && strs.Length == DataLength)
            {
                for (int i = 0; i < strs.Length; i++)
                {
                    if (!double.TryParse(strs[i], out var r))
                        return new ValidationResult(false, string.Format(ERROR, value));
                }
                return new ValidationResult(true, null);
            }
            return new ValidationResult(false, string.Format(ERROR, value));
        }

        public int DataLength { get; set; } = 2;

        private const string ERROR = "{0} is Invalid data!";
    }
}
