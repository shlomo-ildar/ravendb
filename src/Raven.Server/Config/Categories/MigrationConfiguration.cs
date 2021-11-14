using System.ComponentModel;
using Raven.Server.Config.Attributes;
using Raven.Server.Config.Settings;
using Raven.Client.ServerWide.JavaScript;

namespace Raven.Server.Config.Categories
{
    [ConfigurationCategory(ConfigurationCategoryType.Migration)]
    public class MigrationConfiguration : ConfigurationCategory
    {
        [Description("The full path of the directory containing the Raven.Migrator executable. Setting this option here will disable the ability to set a path in the Studio.")]
        [DefaultValue(null)]
        [ConfigurationEntry("Migration.MigratorPath", ConfigurationEntryScope.ServerWideOnly)]
        public PathSetting MigratorPath { get; set; }
    }
}
