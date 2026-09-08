import { AppHeader } from "../layout/AppHeader";
import { ReportFoundItemWizard } from "../components/ReportFoundItemWizard";

export function ReportFoundItemPage() {
  return (
    <div className="min-h-screen bg-[#FAFAFC]">
      <AppHeader />
      <ReportFoundItemWizard />
    </div>
  );
}
