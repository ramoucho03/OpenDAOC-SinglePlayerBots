using System.Collections.Generic;
using System.Reflection;
using DOL.Database;
using DOL.GS.ServerProperties;
using NUnit.Framework;

using TranslationStore = System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<DOL.Database.LanguageDataObject.eTranslationIdentifier, System.Collections.Generic.Dictionary<string, DOL.Database.LanguageDataObject>>>;

namespace DOL.Language.Tests
{
    [TestFixture]
    public class UT_LanguageMgr
    {
        private static readonly FieldInfo SoleInstanceField = typeof(LanguageMgr).GetField("_soleInstance", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo TranslationsField = typeof(LanguageMgr).GetField("<Translations>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static);

        private LanguageMgr _previousLanguageMgr;
        private TranslationStore _previousTranslations;
        private string _previousServerLanguage;
        private string _previousDatabaseLanguage;

        [SetUp]
        public void SetUp()
        {
            _previousLanguageMgr = (LanguageMgr)SoleInstanceField.GetValue(null);
            _previousTranslations = (TranslationStore)TranslationsField.GetValue(null);
            _previousServerLanguage = Properties.SERV_LANGUAGE;
            _previousDatabaseLanguage = Properties.DB_LANGUAGE;

            SoleInstanceField.SetValue(null, new LanguageMgr());
            TranslationsField.SetValue(null, new TranslationStore());
        }

        [TearDown]
        public void TearDown()
        {
            SoleInstanceField.SetValue(null, _previousLanguageMgr);
            TranslationsField.SetValue(null, _previousTranslations);
            Properties.SERV_LANGUAGE = _previousServerLanguage;
            Properties.DB_LANGUAGE = _previousDatabaseLanguage;
        }

        [Test]
        public void TryGetTranslation_WhenFrenchIsDefaultAndFrenchKeyIsMissing_FallsBackToEnglish()
        {
            Properties.SERV_LANGUAGE = "FR";
            RegisterSystemTranslation("EN", "Tests.LanguageFallback.Message", "English fallback");

            bool found = LanguageMgr.TryGetTranslation(out string translation, "FR", "Tests.LanguageFallback.Message");

            Assert.That(found, Is.True);
            Assert.That(translation, Is.EqualTo("English fallback"));
        }

        [Test]
        public void TryGetTranslation_WhenFrenchTranslationExists_UsesFrenchBeforeEnglishFallback()
        {
            Properties.SERV_LANGUAGE = "FR";
            RegisterSystemTranslation("EN", "Tests.LanguageFallback.Message", "English fallback");
            RegisterSystemTranslation("FR", "Tests.LanguageFallback.Message", "Traduction francaise");

            bool found = LanguageMgr.TryGetTranslation(out string translation, "FR", "Tests.LanguageFallback.Message");

            Assert.That(found, Is.True);
            Assert.That(translation, Is.EqualTo("Traduction francaise"));
        }

        [Test]
        public void TryGetTranslation_WhenFrenchIsDefaultButDatabaseLanguageIsEnglish_ReturnsObjectTranslation()
        {
            Properties.SERV_LANGUAGE = "FR";
            Properties.DB_LANGUAGE = "EN";
            RegisterNpcTranslation("FR", "Tests.LanguageFallback.Npc", "Marchand");

            bool found = LanguageMgr.TryGetTranslation(out LanguageDataObject translation, "FR", new TestTranslatableObject());

            Assert.That(found, Is.True);
            Assert.That(((DbLanguageGameNpc)translation).Name, Is.EqualTo("Marchand"));
        }

        [Test]
        public void TryGetTranslation_WhenRequestedLanguageMatchesDatabaseLanguage_SkipsObjectTranslation()
        {
            Properties.SERV_LANGUAGE = "FR";
            Properties.DB_LANGUAGE = "FR";
            RegisterNpcTranslation("FR", "Tests.LanguageFallback.Npc", "Marchand");

            bool found = LanguageMgr.TryGetTranslation(out LanguageDataObject translation, "FR", new TestTranslatableObject());

            Assert.That(found, Is.False);
            Assert.That(translation, Is.Null);
        }

        private static void RegisterSystemTranslation(string language, string translationId, string text)
        {
            LanguageMgr.RegisterLanguageDataObject(new DbLanguageSystem
            {
                Language = language,
                TranslationId = translationId,
                Text = text
            });
        }

        private static void RegisterNpcTranslation(string language, string translationId, string name)
        {
            LanguageMgr.RegisterLanguageDataObject(new DbLanguageGameNpc
            {
                Language = language,
                TranslationId = translationId,
                Name = name
            });
        }

        private sealed class TestTranslatableObject : ITranslatableObject
        {
            public string TranslationId { get; set; } = "Tests.LanguageFallback.Npc";

            public LanguageDataObject.eTranslationIdentifier TranslationIdentifier => LanguageDataObject.eTranslationIdentifier.eNPC;
        }
    }
}
