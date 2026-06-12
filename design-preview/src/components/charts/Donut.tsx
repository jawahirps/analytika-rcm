import { Cell, Pie, PieChart, ResponsiveContainer, Tooltip } from "recharts";

const palette = ["#F43F5E", "#F59E0B", "#6366F1", "#06B6D4", "#8B5CF6", "#22C55E"];

export function Donut({
  data,
}: {
  data: { label: string; value: number }[];
}) {
  return (
    <div className="h-64 w-full">
      <ResponsiveContainer width="100%" height="100%">
        <PieChart>
          <Pie
            data={data}
            dataKey="value"
            nameKey="label"
            innerRadius={56}
            outerRadius={92}
            paddingAngle={2}
            stroke="var(--surface)"
            strokeWidth={2}
          >
            {data.map((_, i) => (
              <Cell key={i} fill={palette[i % palette.length]} />
            ))}
          </Pie>
          <Tooltip
            contentStyle={{
              background: "var(--surface)",
              border: "1px solid var(--border)",
              borderRadius: 10,
              color: "var(--text)",
              fontSize: 12,
            }}
          />
        </PieChart>
      </ResponsiveContainer>
    </div>
  );
}
