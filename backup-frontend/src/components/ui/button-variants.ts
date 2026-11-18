import { cva } from "class-variance-authority"

export const buttonVariants = cva(
  "group/button inline-flex min-h-control pointer-coarse:min-h-11 min-w-control pointer-coarse:min-w-11 max-w-full shrink-0 items-center justify-center rounded-lg border bg-clip-padding text-center text-body font-medium whitespace-normal sm:whitespace-nowrap transition-[background-color,border-color,color] duration-150 select-none active:not-aria-[haspopup]:translate-y-px disabled:pointer-events-none disabled:opacity-50 aria-invalid:border-error aria-invalid:ring-2 aria-invalid:ring-error-border [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
  {
    variants: {
      variant: {
        default: "border-transparent bg-primary text-primary-foreground hover:bg-primary-hover",
        outline:
          "border-input bg-surface text-text hover:bg-surface-2 aria-expanded:bg-surface-2",
        secondary:
          "border-transparent bg-surface-2 text-text hover:bg-border aria-expanded:bg-border",
        ghost:
          "border-transparent hover:bg-surface-2 aria-expanded:bg-surface-2",
        destructive:
          "border-error-border bg-surface text-error hover:bg-error-subtle",
        link: "border-transparent text-primary underline-offset-4 hover:underline",
      },
      size: {
        default:
          "gap-2 px-3 py-1 has-data-[icon=inline-end]:pr-3 has-data-[icon=inline-start]:pl-3",
        sm: "gap-1.5 px-3 py-1 has-data-[icon=inline-end]:pr-1.5 has-data-[icon=inline-start]:pl-1.5 [&_svg:not([class*='size-'])]:size-3.5",
        lg: "gap-2 px-6 py-1 has-data-[icon=inline-end]:pr-4 has-data-[icon=inline-start]:pl-4",
        icon: "size-control pointer-coarse:size-11",
      },
    },
    defaultVariants: {
      variant: "default",
      size: "default",
    },
  }
)
