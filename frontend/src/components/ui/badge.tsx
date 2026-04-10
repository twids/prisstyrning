import * as React from "react"
import { cva, type VariantProps } from "class-variance-authority"

import { cn } from "@/lib/utils"

const badgeVariants = cva(
  "inline-flex items-center rounded-full border px-2.5 py-0.5 text-xs font-semibold transition-colors focus:outline-none focus:ring-2 focus:ring-ring focus:ring-offset-2",
  {
    variants: {
      variant: {
        default:
          "border-transparent bg-primary text-primary-foreground hover:bg-primary/80",
        secondary:
          "border-transparent bg-secondary text-secondary-foreground hover:bg-secondary/80",
        destructive:
          "border-transparent bg-destructive text-destructive-foreground hover:bg-destructive/80",
        outline: "text-foreground",
        comfort:
          "border-transparent bg-[hsl(var(--comfort))]/20 text-[hsl(var(--comfort))]",
        eco:
          "border-transparent bg-[hsl(var(--eco))]/20 text-[hsl(var(--eco))]",
        "turn-off":
          "border-transparent bg-[hsl(var(--turn-off))]/20 text-[hsl(var(--turn-off))]",
        warning:
          "border-transparent bg-[hsl(var(--warning))]/20 text-[hsl(var(--warning))]",
        success:
          "border-transparent bg-[hsl(var(--success))]/20 text-[hsl(var(--success))]",
      },
    },
    defaultVariants: {
      variant: "default",
    },
  }
)

export interface BadgeProps
  extends React.HTMLAttributes<HTMLDivElement>,
    VariantProps<typeof badgeVariants> {}

function Badge({ className, variant, ...props }: BadgeProps) {
  return (
    <div className={cn(badgeVariants({ variant }), className)} {...props} />
  )
}

export { Badge, badgeVariants }
