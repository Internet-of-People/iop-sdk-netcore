## Component Manager

The role of the component manager is to manage the life cycle of other components. This is a very simple task that consists of calling the two methods that each component has to implement:

 * The initialization method that starts up everything the component needs for doing its work. It is called during the profile server startup.
 * The shutdown method that terminates the execution of all parts of the component and frees resources used by the component. It is called during the termination of the profile server.

The component manager also manages the global shutdown signalling mechanism, which helps proper termination of each component during the shutdown.


## Cron Component

This component is the second component that has to be initialized just after the configuration component is ready. 
All other components can then subscribe their cron jobs with this component, which then cares about their regular execution.
