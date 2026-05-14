using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace DOL.Language.Tests
{
    [TestFixture]
    public class UT_LanguageFiles
    {
        private static readonly string[] CriticalLanguages = ["EN", "FR"];
        private static readonly string[] FrenchCombatAndConsoleKeys =
        [
            "DetailDisplayHandler.HandlePacket.YouHit",
            "GameLiving.Attack.Block",
            "GameLiving.AttackData.AttackType.Attacks",
            "GameLiving.AttackData.AttackType.Shoots",
            "GameLiving.AttackData.Blocked",
            "GameLiving.AttackData.BlocksYou",
            "GameLiving.AttackData.CriticallyHitsForDamage",
            "GameLiving.AttackData.Evaded",
            "GameLiving.AttackData.Fumbled",
            "GameLiving.AttackData.GainEndurancePoints",
            "GameLiving.AttackData.GainPowerPoints",
            "GameLiving.AttackData.HitsForDamage",
            "GameLiving.AttackData.InvisibleToYou",
            "GameLiving.AttackData.Misses",
            "GameLiving.AttackData.Parried",
            "GameLiving.AttackData.StepsInFront",
            "GameLiving.AttackData.YouBlock",
            "GameLiving.AttackData.YourCriticallyHits",
            "GameLiving.AttackData.YourHits",
            "GameLiving.AttackData.YouStepInFront",
            "GameLiving.CalculateEnemyAttackResult.BlowAbsorbed",
            "GameLiving.CalculateEnemyAttackResult.BlowIntercepted",
            "GameLiving.CalculateEnemyAttackResult.BlowPenetrated",
            "GameLiving.CalculateEnemyAttackResult.StrikeAbsorbed",
            "GameLiving.CalculateEnemyAttackResult.StrikeIntercepted",
            "GameLiving.CalculateEnemyAttackResult.YouAttempt",
            "GameLiving.CalculateEnemyAttackResult.YouHaveProtected",
            "GameLiving.CalculateEnemyAttackResult.YourPetAttempts",
            "GameLiving.CalculateEnemyAttackResult.YouWereProtected",
            "GameLiving.CheckWeaponMagicalEffect.Protected",
            "GameLiving.StartWeaponMagicalEffect.NotPowerful",
            "GameLiving.TryBlock.Blocking",
            "GameLiving.TryBlock.Engage",
            "GamePlayer.Attack.InterruptedCrafting",
            "SpellHandler.CancelEffect",
            "SpellHandler.CancelPulsingSpell",
            "SpellHandler.CastSpell.Msg.LivingCastsSpell",
            "SpellHandler.CastSpell.Msg.PetBeginsCasting",
            "SpellHandler.CastSpell.Msg.PetCastSpell",
            "SpellHandler.CastSpell.Msg.YouBeginCasting",
            "SpellHandler.CastSpell.Msg.YouCastSpell",
        ];

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private static readonly Regex PlaceholderRegex = new(@"\{([0-9]+)(?:[^}]*)\}", RegexOptions.Compiled);

        [Test]
        public void CriticalLanguageFiles_ShouldBeValidUtf8()
        {
            List<string> invalidFiles = [];

            foreach (string file in EnumerateCriticalLanguageFiles())
            {
                try
                {
                    _ = File.ReadAllText(file, StrictUtf8);
                }
                catch (DecoderFallbackException ex)
                {
                    invalidFiles.Add($"{ToRelativePath(file)}: {ex.Message}");
                }
            }

            Assert.That(invalidFiles, Is.Empty, string.Join(Environment.NewLine, invalidFiles));
        }

        [Test]
        public void CriticalLanguageFiles_ShouldNotContainConflictingDuplicateKeys()
        {
            List<string> conflicts = [];

            foreach (string language in CriticalLanguages)
            {
                IEnumerable<IGrouping<string, TranslationEntry>> duplicateGroups = ReadLanguage(language)
                    .GroupBy(entry => entry.Key)
                    .Where(group => group.Count() > 1);

                foreach (IGrouping<string, TranslationEntry> group in duplicateGroups)
                {
                    if (group.Select(entry => entry.Text).Distinct(StringComparer.Ordinal).Count() <= 1)
                        continue;

                    conflicts.Add($"{language} {group.Key}:{Environment.NewLine}{FormatEntries(group)}");
                }
            }

            Assert.That(conflicts, Is.Empty, string.Join($"{Environment.NewLine}{Environment.NewLine}", conflicts));
        }

        [Test]
        public void FrenchTranslations_ShouldKeepPlaceholdersCompatibleWithEnglish()
        {
            Dictionary<string, TranslationEntry> english = ReadFirstEntriesByKey("EN");
            Dictionary<string, TranslationEntry> french = ReadFirstEntriesByKey("FR");
            List<string> mismatches = [];

            foreach ((string key, TranslationEntry englishEntry) in english)
            {
                if (!french.TryGetValue(key, out TranslationEntry frenchEntry))
                    continue;

                string englishPlaceholders = string.Join(",", GetPlaceholders(englishEntry.Text));
                string frenchPlaceholders = string.Join(",", GetPlaceholders(frenchEntry.Text));

                if (!string.Equals(englishPlaceholders, frenchPlaceholders, StringComparison.Ordinal))
                    mismatches.Add($"{key}: EN [{englishPlaceholders}] FR [{frenchPlaceholders}] at {frenchEntry.File}:{frenchEntry.Line}");
            }

            Assert.That(mismatches, Is.Empty, string.Join(Environment.NewLine, mismatches));
        }

        [Test]
        public void FrenchCombatAndConsoleTranslations_ShouldExist()
        {
            Dictionary<string, TranslationEntry> french = ReadFirstEntriesByKey("FR");
            List<string> missing = FrenchCombatAndConsoleKeys
                .Where(key => !french.ContainsKey(key))
                .ToList();

            Assert.That(missing, Is.Empty, string.Join(Environment.NewLine, missing));
        }

        private static IEnumerable<string> EnumerateCriticalLanguageFiles()
        {
            string languageRoot = GetLanguageRoot();

            foreach (string language in CriticalLanguages)
            {
                string path = Path.Combine(languageRoot, language);

                foreach (string file in Directory.EnumerateFiles(path, "*.txt", SearchOption.AllDirectories))
                    yield return file;
            }
        }

        private static Dictionary<string, TranslationEntry> ReadFirstEntriesByKey(string language)
        {
            Dictionary<string, TranslationEntry> entries = new(StringComparer.Ordinal);

            foreach (TranslationEntry entry in ReadLanguage(language))
                entries.TryAdd(entry.Key, entry);

            return entries;
        }

        private static IEnumerable<TranslationEntry> ReadLanguage(string language)
        {
            string path = Path.Combine(GetLanguageRoot(), language);

            foreach (string file in Directory.EnumerateFiles(path, "*.txt", SearchOption.AllDirectories).OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
            {
                string relativePath = ToRelativePath(file);
                string[] lines = File.ReadAllLines(file, StrictUtf8);

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];

                    if (line.StartsWith('#') || !line.Contains(':'))
                        continue;

                    int separatorIndex = line.IndexOf(':');
                    string key = line[..separatorIndex];
                    string text = line[(separatorIndex + 1)..].Replace("\t", " ").Trim();

                    yield return new TranslationEntry(key, text, relativePath, i + 1);
                }
            }
        }

        private static string[] GetPlaceholders(string text)
        {
            return PlaceholderRegex.Matches(text)
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(placeholder => placeholder, StringComparer.Ordinal)
                .ToArray();
        }

        private static string FormatEntries(IEnumerable<TranslationEntry> entries)
        {
            return string.Join(Environment.NewLine, entries.Select(entry => $"  {entry.File}:{entry.Line} => {entry.Text}"));
        }

        private static string GetLanguageRoot()
        {
            return Path.Combine(GetRepositoryRoot(), "GameServer", "language");
        }

        private static string GetRepositoryRoot()
        {
            DirectoryInfo current = new(TestContext.CurrentContext.TestDirectory);

            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "GameServer", "language")))
                    return current.FullName;

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not find repository root containing GameServer/language.");
        }

        private static string ToRelativePath(string path)
        {
            return Path.GetRelativePath(GetRepositoryRoot(), path).Replace('\\', '/');
        }

        private sealed record TranslationEntry(string Key, string Text, string File, int Line);
    }
}
