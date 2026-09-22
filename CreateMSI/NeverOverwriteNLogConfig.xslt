<?xml version="1.0" encoding="UTF-8"?>
<!-- Applied by heat.exe (via the HarvestDirectory Transforms metadata in CreateMSI.Web.wixproj)
     to the harvested WebAppFiles component group. nlog.config is a per-machine config file admins
     may hand-edit after install, so it must never be clobbered by an upgrade/repair.

     Just marking the harvested Component NeverOverwrite="yes" isn't enough: this project sets
     HarvestDirectoryGenerateGuidsNow="true", so heat.exe mints a brand-new Component Guid for
     nlog.config on every single build. A new Guid means Windows Installer treats it as a totally
     unrelated component from the one the previous version installed, so on upgrade the OLD
     product's uninstall (RemoveExistingProducts, scheduled early in Product.wxs) deletes the file
     outright before the new product ever gets a chance to skip overwriting it.

     So instead this transform strips the harvested nlog.config Component (and its ComponentRef)
     out entirely. Product.wxs authors it manually instead, with a Guid that stays fixed across
     every build: that's what lets Windows Installer recognize it as the *same* component across
     versions and keep it alive (and untouched, via NeverOverwrite) through the upgrade. -->
<xsl:stylesheet version="1.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:wix="http://schemas.microsoft.com/wix/2006/wi"
                exclude-result-prefixes="wix">

  <xsl:output method="xml" indent="yes" />

  <xsl:template match="@*|node()">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()" />
    </xsl:copy>
  </xsl:template>

  <xsl:template match="wix:Component[wix:File[@Source='SourceDir\nlog.config']]" />

  <!-- Match patterns can't reference the document root (//...) via current()/variables, so the
       ComponentRef pointing at the nlog.config Component is filtered out here via apply-templates
       select instead, where a full XPath comparison is allowed. -->
  <xsl:template match="wix:ComponentGroup">
    <xsl:copy>
      <xsl:copy-of select="@*" />
      <xsl:apply-templates
        select="wix:ComponentRef[not(@Id = //wix:Component[wix:File[@Source='SourceDir\nlog.config']]/@Id)]" />
    </xsl:copy>
  </xsl:template>

</xsl:stylesheet>
