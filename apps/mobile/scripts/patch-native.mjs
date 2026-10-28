import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";

const root = resolve(import.meta.dirname, "..");

const manifest = resolve(root, "android/app/src/main/AndroidManifest.xml");
if (existsSync(manifest)) {
  let xml = readFileSync(manifest, "utf8");
  if (!xml.includes('android:scheme="vusos"')) {
    xml = xml.replace(
      /(<activity[^>]*android:name="\.MainActivity"[^>]*>)/,
      `$1
            <intent-filter>
                <action android:name="android.intent.action.VIEW" />
                <category android:name="android.intent.category.DEFAULT" />
                <category android:name="android.intent.category.BROWSABLE" />
                <data android:scheme="vusos" />
            </intent-filter>`
    );
    writeFileSync(manifest, xml);
    console.log("AndroidManifest.xml: dodano intent-filter vusos://");
  }
}

const plist = resolve(root, "ios/App/App/Info.plist");
if (existsSync(plist)) {
  let xml = readFileSync(plist, "utf8");
  if (!xml.includes("<string>vusos</string>")) {
    xml = xml.replace(
      /<\/dict>\s*<\/plist>\s*$/,
      `	<key>CFBundleURLTypes</key>
	<array>
		<dict>
			<key>CFBundleURLName</key>
			<string>pl.m4tk.vusos</string>
			<key>CFBundleURLSchemes</key>
			<array>
				<string>vusos</string>
			</array>
		</dict>
	</array>
</dict>
</plist>
`
    );
    writeFileSync(plist, xml);
    console.log("Info.plist: dodano CFBundleURLTypes vusos://");
  }
}
