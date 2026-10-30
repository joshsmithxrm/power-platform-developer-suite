namespace PPDS.Cli.Tests.Services.Forms;

internal static class TimelineFormFixture
{
    // Reconstructed from the Timeline fragment reported in #1465, not a live export.
    // Include modern root attributes, an unbraced section GUID, a normal field, and
    // adjacent Timeline parameters so both validation gates and edit preservation run.
    internal const string Xml = """
        <form showImage="true" headerdensity="HighWithControls">
          <tabs>
            <tab name="tab_general" id="{22222222-2222-2222-2222-222222222222}" IsUserDefined="1" locklevel="0" showlabel="1" expanded="1" visible="1" availableforphone="1">
              <labels><label description="My Tab" languagecode="1033" /></labels>
              <columns><column width="100%"><sections>
                <section name="section_general" id="44444444-4444-4444-4444-444444444444" IsUserDefined="1" locklevel="0" showlabel="1" visible="1" availableforphone="1" columns="1">
                  <labels><label description="General" languagecode="1033" /></labels>
                  <rows>
                    <row><cell id="{66666666-6666-6666-6666-666666666666}">
                      <control id="name" datafieldname="name" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" />
                    </cell></row>
                    <row><cell id="{77777777-7777-7777-7777-777777777777}" showlabel="false" rowspan="23">
                      <control id="notescontrol" classid="{06375649-c143-495e-a496-c962e5b4488e}" disabled="false">
                        <parameters>
                          <UClientActivityCardMap>{"email":{"title":"subject"}}</UClientActivityCardMap>
                          <UClientActivitiesConfigurationJSON>{"email":{"showStatus":true,"canCreate":true,"fieldsConfig":{"subject":{"label":"Subject &amp; details &lt;preview&gt;"}}}}</UClientActivitiesConfigurationJSON>
                          <UClientActivities>email,appointment,task</UClientActivities>
                          <UClientDisplayActivityUsing>1</UClientDisplayActivityUsing>
                          <UClientShowFilterPane>true</UClientShowFilterPane>
                          <UClientRecordPerPage>10</UClientRecordPerPage>
                        </parameters>
                      </control>
                    </cell></row>
                  </rows>
                </section>
              </sections></column></columns>
            </tab>
          </tabs>
        </form>
        """;
}
