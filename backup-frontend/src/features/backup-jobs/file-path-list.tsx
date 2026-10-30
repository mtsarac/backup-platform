import { useState } from 'react'
import { TextArea } from '@/components/ui/text-input'
import { tx } from '@/lib/i18n'
import { Field } from '@/components/ui/field'

export function FilePathList({ id, label, paths, onChange, error }: { id: string; label: string; paths: readonly string[]; onChange: (paths: string[]) => void; error?: string }) {
  const [value, setValue] = useState(() => paths.join('\n'))
  return <Field id={id} label={label} error={error} hint={tx('Her satıra bir göreli yol yazın; ayırıcı olarak / kullanın. Mutlak yol ve .. kabul edilmez.')}>
    {control => <TextArea {...control} rows={4} value={value} onChange={event => {
      setValue(event.target.value)
      onChange(event.target.value.split(/\r?\n/).filter(path => path.trim().length > 0))
    }} />}
  </Field>
}
