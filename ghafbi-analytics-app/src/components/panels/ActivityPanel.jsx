import { Panel, PanelHead } from "@/components/ui/Panel";

export function ActivityPanel({ rows }) {
  return (
    <Panel className="activity-panel">
      <PanelHead compact title="Recent Activity" action={<button type="button" className="text-btn">View all</button>} />
      <div className="table-scroll">
        <table>
          <thead>
            <tr>
              <th>Item</th>
              <th>Area</th>
              <th>Time</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.join("-")}>
                {row.map((cell, index) => (
                  <td key={`${row.join("-")}-${cell}`} className={index === 3 ? "status-cell" : ""}>
                    {cell}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Panel>
  );
}
