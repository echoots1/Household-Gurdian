// psl ships types but its package.json "exports" map hides them from bundler resolution.
declare module "psl" {
  const psl: { get(domain: string): string | null; isValid(domain: string): boolean };
  export default psl;
}
